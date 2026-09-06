using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

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
            TestProjectHelper.DeleteFile(path);
        }
    }

    [Fact]
    public void PrivateLogFile_OpenAppend_OnUnixRejectsSymlinkTargets_Issue3824()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = TestProjectHelper.CreateTempProject("cdidx_private_log_symlink");
        var target = Path.Combine(directory, "target.log");
        var link = Path.Combine(directory, "link.log");
        try
        {
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
            TestProjectHelper.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TryStart_RepositoryConfiguredOrdinaryDirectoryWritesSuccessfully_Issue5181()
    {
        var workspace = TestProjectHelper.CreateTempProject("cdidx_global_log_config_5181");
        var logDirectory = Path.Combine(workspace, "state", "logs");
        var sourceVariable = CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + "CDIDX_GLOBAL_TOOL_LOG_DIR";
        using var environment = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            sourceVariable);
        environment.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
        environment.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
        environment.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", null);
        environment.Set(sourceVariable, null);
        try
        {
            File.WriteAllText(
                Path.Combine(workspace, CdidxConfigFile.FileName),
                """{ "global_tool_log_dir": "state/logs" }""");
            var config = CdidxConfigFile.Load(workspace);
            Assert.True(config.Loaded);

            using (CdidxEnvironment.Push(config.Settings, config.Sources))
            using (var session = GlobalToolLog.TryStartForTesting(["status"], "test"))
            {
                Assert.NotNull(session);
                GlobalToolLog.Info("repository_configured_log_boundary_ok");
            }

            var logPath = Assert.Single(Directory.GetFiles(logDirectory, "stderr-*.log"));
            Assert.Contains("repository_configured_log_boundary_ok", File.ReadAllText(logPath), StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    DataDirectorySecurity.PrivateDirectoryMode,
                    File.GetUnixFileMode(logDirectory) & PermissionBits);
                Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(logPath) & PermissionBits);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workspace);
        }
    }

    [Fact]
    public void TryStart_RepositoryConfiguredAncestorSwapIsRejectedWithoutFallbackWrite_Issue5181()
    {
        var workspace = TestProjectHelper.CreateTempProject("cdidx_global_log_race_5181");
        var outside = TestProjectHelper.CreateTempProject("cdidx_global_log_outside_5181");
        var safeDirectory = Path.Combine(workspace, "safe");
        var sourceVariable = CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + "CDIDX_GLOBAL_TOOL_LOG_DIR";
        using var environment = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            sourceVariable);
        environment.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
        environment.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
        environment.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", null);
        environment.Set(sourceVariable, null);
        try
        {
            Directory.CreateDirectory(safeDirectory);
            File.WriteAllText(
                Path.Combine(workspace, CdidxConfigFile.FileName),
                """{ "global_tool_log_dir": "safe/new/logs" }""");
            var config = CdidxConfigFile.Load(workspace);
            Assert.True(config.Loaded);
            var swapped = false;
            RepositoryOutputPathBoundary.BeforeMutationForTesting = (operation, _) =>
            {
                if (swapped || operation != "create_directory")
                    return;
                swapped = true;
                Directory.Delete(safeDirectory);
                Directory.CreateSymbolicLink(safeDirectory, outside);
            };

            using var scopedConfig = CdidxEnvironment.Push(config.Settings, config.Sources);
            var capture = ConsoleCapture.Capture(() =>
            {
                using var session = GlobalToolLog.TryStartForTesting(["status"], "test");
                return session is null ? 0 : 1;
            });

            Assert.Equal(0, capture.ExitCode);
            Assert.Contains("global_tool_log_dir", capture.Stderr, StringComparison.Ordinal);
            Assert.Contains(RepositoryOutputPathBoundary.UnsafeReason, capture.Stderr, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(outside, "new")));
            Assert.Empty(Directory.GetFiles(outside, "stderr-*.log", SearchOption.AllDirectories));
        }
        finally
        {
            RepositoryOutputPathBoundary.BeforeMutationForTesting = null;
            DeleteDirectoryLink(safeDirectory);
            TestProjectHelper.DeleteDirectory(workspace);
            TestProjectHelper.DeleteDirectory(outside);
        }
    }

    [Fact]
    public void QueryTrace_RepositoryConfiguredAncestorSwapDoesNotWriteOutsideWorkspace_Issue5181()
    {
        var workspace = TestProjectHelper.CreateTempProject("cdidx_query_trace_race_5181");
        var outside = TestProjectHelper.CreateTempProject("cdidx_query_trace_outside_5181");
        var safeDirectory = Path.Combine(workspace, "safe");
        var originalDirectory = Path.Combine(workspace, "safe-original");
        var logDirectory = Path.Combine(safeDirectory, "logs");
        var sourceVariable = CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + "CDIDX_GLOBAL_TOOL_LOG_DIR";
        using var environment = EnvironmentVariableScope.Capture("CDIDX_GLOBAL_TOOL_LOG_DIR", sourceVariable);
        environment.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", null);
        environment.Set(sourceVariable, null);
        try
        {
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(
                Path.Combine(workspace, CdidxConfigFile.FileName),
                """{ "global_tool_log_dir": "safe/logs" }""");
            var config = CdidxConfigFile.Load(workspace);
            Assert.True(config.Loaded);
            var swapped = false;
            RepositoryOutputPathBoundary.BeforeMutationForTesting = (operation, path) =>
            {
                if (swapped
                    || operation != "open_append"
                    || !Path.GetFileName(path).StartsWith("query-trace-", StringComparison.Ordinal))
                {
                    return;
                }

                swapped = true;
                Directory.Move(safeDirectory, originalDirectory);
                Directory.CreateSymbolicLink(safeDirectory, outside);
            };

            using var scopedConfig = CdidxEnvironment.Push(config.Settings, config.Sources);
            ProgramRunner.EmitQueryTrace(
                "file",
                "search",
                ["needle"],
                DateTimeOffset.UtcNow,
                System.Diagnostics.Stopwatch.StartNew(),
                CommandExitCodes.Success,
                resultCount: 0);

            Assert.True(swapped);
            Assert.Empty(Directory.GetFiles(outside, "query-trace-*.jsonl", SearchOption.AllDirectories));
        }
        finally
        {
            RepositoryOutputPathBoundary.BeforeMutationForTesting = null;
            DeleteDirectoryLink(safeDirectory);
            TestProjectHelper.DeleteDirectory(workspace);
            TestProjectHelper.DeleteDirectory(outside);
        }
    }

    [Fact]
    public void LastFailure_RepositoryConfiguredAncestorSwapDoesNotWriteOutsideWorkspace_Issue5181()
    {
        var workspace = TestProjectHelper.CreateTempProject("cdidx_last_failure_race_5181");
        var outside = TestProjectHelper.CreateTempProject("cdidx_last_failure_outside_5181");
        var safeDirectory = Path.Combine(workspace, "safe");
        var originalDirectory = Path.Combine(workspace, "safe-original");
        var logDirectory = Path.Combine(safeDirectory, "logs");
        var sourceVariable = CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + "CDIDX_GLOBAL_TOOL_LOG_DIR";
        using var environment = EnvironmentVariableScope.Capture("CDIDX_GLOBAL_TOOL_LOG_DIR", sourceVariable);
        environment.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", null);
        environment.Set(sourceVariable, null);
        try
        {
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(
                Path.Combine(workspace, CdidxConfigFile.FileName),
                """{ "global_tool_log_dir": "safe/logs" }""");
            var config = CdidxConfigFile.Load(workspace);
            Assert.True(config.Loaded);
            var swapped = false;
            RepositoryOutputPathBoundary.BeforeMutationForTesting = (operation, _) =>
            {
                if (swapped || operation != "write_private_text")
                    return;

                swapped = true;
                Directory.Move(safeDirectory, originalDirectory);
                Directory.CreateSymbolicLink(safeDirectory, outside);
            };

            using var scopedConfig = CdidxEnvironment.Push(config.Settings, config.Sources);
            var persisted = LastFailureEventStore.TryPersist(
                ["search"],
                "test",
                CommandExitCodes.UnhandledException,
                new InvalidOperationException("test failure"),
                DateTimeOffset.UtcNow,
                dbPathForTesting: Path.Combine(workspace, ".cdidx", "codeindex.db"),
                workspacePathForTesting: workspace);

            Assert.False(persisted);
            Assert.True(swapped);
            Assert.False(File.Exists(Path.Combine(outside, LastFailureEventStore.FileName)));
        }
        finally
        {
            RepositoryOutputPathBoundary.BeforeMutationForTesting = null;
            DeleteDirectoryLink(safeDirectory);
            TestProjectHelper.DeleteDirectory(workspace);
            TestProjectHelper.DeleteDirectory(outside);
        }
    }

    [Fact]
    public void PrivateLogFile_HardenExisting_CapsBestEffortWork_Issue3027()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = TestProjectHelper.CreateTempProject("cdidx_private_log_harden");
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
            TestProjectHelper.DeleteDirectory(directory);
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
        var directory = TestProjectHelper.CreateTempProject("cdidx_private_log_prune");
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
            TestProjectHelper.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void PrivateLogFile_PruneOldFiles_DeleteFailureReportsDiagnosticAndContinues_Issue3962()
    {
        var directory = TestProjectHelper.CreateTempProject("cdidx_private_log_prune_diag");
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
            TestProjectHelper.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void PrivateLogFile_TryRotateSlots_ReplacesExistingSlotsAtomicallyWherePossible()
    {
        var directory = TestProjectHelper.CreateTempProject("cdidx_private_log_rotate");
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
            TestProjectHelper.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void PrivateLogFile_TryRotateSlots_ReportsPostReplaceFlushFailure_Issue3776()
    {
        var directory = TestProjectHelper.CreateTempProject("cdidx_private_log_rotate_flush");
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
            TestProjectHelper.DeleteDirectory(directory);
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
            TestProjectHelper.DeleteDirectory(logRoot);
            TestProjectHelper.DeleteFile(capturedLogPath);
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
            "--private-key=visible4175",
            "--session_cookie",
            "session-visible4175",
        ]);

        Assert.Contains("--github-token <redacted>", formatted);
        Assert.Contains("--serviceCredential=<redacted>", formatted);
        Assert.Contains("--private-key=<redacted>", formatted);
        Assert.Contains("--session_cookie <redacted>", formatted);
        Assert.DoesNotContain("gh-secret", formatted);
        Assert.DoesNotContain("credential-secret", formatted);
        Assert.DoesNotContain("visible4175", formatted);
        Assert.DoesNotContain("session-visible4175", formatted);
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
        var root = TestProjectHelper.CreateTempProject("cdidx_log_probe");
        var fileCandidate = Path.Combine(root, "not-a-directory");
        var stateHome = Path.Combine(root, "state");
        try
        {
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
            TestProjectHelper.DeleteDirectory(root);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryStart_WritesInvariantUtcTimestampAndStackTrace(bool changingDefaultDatabase)
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_global_log_5264");
        var logRoot = Path.Combine(root, "logs");
        var defaultDataDirectory = Path.Combine(root, "ambient");
        Directory.CreateDirectory(defaultDataDirectory);
        var defaultDbPath = Path.Combine(defaultDataDirectory, "codeindex.db");
        var previousCulture = CultureInfo.CurrentCulture;
        var previousSnapshotHook = DbConnectionFactory.QueryOnlySnapshotCapturedForTesting;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var sourceVariable = CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + "CDIDX_GLOBAL_TOOL_LOG_DIR";
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_FORCE_GLOBAL_TOOL_LOG",
                "CDIDX_DISABLE_PERSISTENT_LOG",
                "CDIDX_GLOBAL_TOOL_LOG_DIR",
                "CDIDX_LOG_FORMAT",
                CdidxConfigFile.DisableEnvVar,
                DbPathResolver.DataDirEnvironmentVariable,
                sourceVariable);
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);
            env.Set("CDIDX_LOG_FORMAT", "text");
            env.Set(CdidxConfigFile.DisableEnvVar, "1");
            env.Set(DbPathResolver.DataDirEnvironmentVariable, defaultDataDirectory);
            env.Set(sourceVariable, null);

            using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = defaultDbPath,
                Pooling = false,
            }.ConnectionString);
            writer.Open();
            using var mutation = writer.CreateCommand();
            mutation.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE changes(value INTEGER); INSERT INTO changes VALUES (0);";
            mutation.ExecuteNonQuery();
            mutation.CommandText = "UPDATE changes SET value = value + 1;";
            var snapshotAttempts = 0;
            DbConnectionFactory.QueryOnlySnapshotCapturedForTesting = changingDefaultDatabase
                ? () =>
                {
                    snapshotAttempts++;
                    mutation.ExecuteNonQuery();
                }
            : null;

            if (changingDefaultDatabase)
            {
                var failure = Assert.Throws<CodeIndexException>(() =>
                {
                    using var probe = DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
                        defaultDbPath, pooling: false, out _, out _);
                });
                Assert.Equal("query_only_wal_changed", failure.Code);
                Assert.Equal(3, snapshotAttempts);
                snapshotAttempts = 0;
            }

            // Failure-event provenance resolves a DB even though dispatch never runs.
            // Pin an absent private DB so a changing ambient WAL cannot suppress the report hint.
            var isolatedDbPath = Path.Combine(root, "isolated", ".cdidx", "codeindex.db");
            var (exitCode, _, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["search", "Needle", "--db", isolatedDbPath],
                appVersion: "test",
                beforeDispatchForTesting: ThrowForGlobalToolLogTest));

            Assert.Equal(CommandExitCodes.UnhandledException, exitCode);
            Assert.Contains("Run `cdidx report`", stderr);
            Assert.Equal(0, snapshotAttempts);
            Assert.False(File.Exists(isolatedDbPath));
            Assert.True(File.Exists(Path.Combine(logRoot, LastFailureEventStore.FileName)));
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
            DbConnectionFactory.QueryOnlySnapshotCapturedForTesting = previousSnapshotHook;
            CultureInfo.CurrentCulture = previousCulture;
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void LogIndexFileFailure_WritesSanitizedStackTraceToPersistentLog()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_global_log_index_failure_{Guid.NewGuid():N}");
        try
        {
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_FORCE_GLOBAL_TOOL_LOG",
                "CDIDX_DISABLE_PERSISTENT_LOG",
                "CDIDX_GLOBAL_TOOL_LOG_DIR");
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);

            using (GlobalToolLog.TryStartForTesting(["index", "."], "test"))
            {
                var captured = Assert.Throws<ArgumentException>(ThrowForIndexFileFailureLogTest);

                try
                {
                    IndexCommandRunner.RethrowPreservingStackTrace(captured);
                }
                catch (Exception ex)
                {
                    IndexCommandRunner.LogIndexFileFailure("index_file_failed", "src/bad\nfile.js", "symbols", ex);
                }
            }

            var logPath = Assert.Single(Directory.GetFiles(logRoot, "stderr-*.log"));
            var log = File.ReadAllText(logPath);
            Assert.Contains("index_file_failed path=src/bad file.js phase=symbols detail=ArgumentException", log);
            Assert.Contains("exception[0] type=System.ArgumentException message=\"argument_error\"", log);
            Assert.Contains("  stack: ", log);
            Assert.Contains(nameof(ThrowForIndexFileFailureLogTest), log);
            Assert.DoesNotContain("secret-token", log);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(logRoot);
        }
    }

    private static void ThrowForIndexFileFailureLogTest()
    {
        throw new ArgumentException("secret-token");
    }

    [Fact]
    public void TryStart_ErrorMirrorIgnoresDisposedOriginalConsoleWriter()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_global_log_disposed_{Guid.NewGuid():N}");
        using var capture = ConsoleCapture.Start(captureError: true);
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
            TestProjectHelper.DeleteDirectory(logRoot);
        }
    }

    [Fact]
    public void TryStart_ErrorMirrorTruncatesLargeWrites_Issue3166()
    {
        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_global_log_large_mirror_{Guid.NewGuid():N}");
        using var visibleError = new StringWriter(CultureInfo.InvariantCulture);
        using var capture = ConsoleCapture.Start(null, visibleError);
        try
        {
            using var env = EnvironmentVariableScope.Capture(
                "CDIDX_FORCE_GLOBAL_TOOL_LOG",
                "CDIDX_DISABLE_PERSISTENT_LOG",
                "CDIDX_GLOBAL_TOOL_LOG_DIR");
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);
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
            TestProjectHelper.DeleteDirectory(logRoot);
        }
    }

    private static void ThrowForGlobalToolLogTest() =>
        throw new InvalidOperationException("global log stack trace test");

    private static void DeleteDirectoryLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Flush() => throw new ObjectDisposedException(nameof(ThrowingTextWriter));

        public override void Write(char value) => throw new ObjectDisposedException(nameof(ThrowingTextWriter));

        public override void Write(string? value) => throw new ObjectDisposedException(nameof(ThrowingTextWriter));

        public override void WriteLine(string? value) => throw new ObjectDisposedException(nameof(ThrowingTextWriter));
    }
}
