using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class ProgramRunnerTests
{
    [Fact]
    public void Run_UnhandledException_ReturnsSanitizedSingleLineError()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["status"],
            appVersion: "1.10.0",
            beforeDispatchForTesting: () => throw new InvalidOperationException("boom")));

        Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
        Assert.Equal(string.Empty, stdout);

        var trimmed = stderr.TrimEnd();
        Assert.Equal(trimmed, stderr.Trim());
        Assert.DoesNotContain(Environment.NewLine, trimmed);
        Assert.DoesNotContain("InvalidOperationException", trimmed);
        Assert.DoesNotContain("CodeIndex.", trimmed);
        Assert.DoesNotContain(" at ", trimmed);
        Assert.DoesNotContain(" in ", trimmed);
        Assert.StartsWith("Error: command failed before it could complete.", trimmed);
    }

    [Fact]
    public void Run_OperationCanceledException_ReturnsCancelledExitCode()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["status"],
            appVersion: "1.10.0",
            beforeDispatchForTesting: () => throw new OperationCanceledException("timeout budget elapsed")));

        Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
        Assert.Equal(string.Empty, stdout);

        var trimmed = stderr.TrimEnd();
        Assert.Equal(trimmed, stderr.Trim());
        Assert.DoesNotContain(Environment.NewLine, trimmed);
        Assert.DoesNotContain("OperationCanceledException", trimmed);
        Assert.DoesNotContain("timeout budget elapsed", trimmed);
        Assert.StartsWith("Error: command cancelled before it could complete.", trimmed);
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_WritesLifecycleAndMirrorsStderr()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var originalForce = Environment.GetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG");
        var originalDisable = Environment.GetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG");
        var originalLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            Environment.SetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            Environment.SetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG", null);
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("Unknown command: definitely-not-a-command", stderr);

            var logPath = Directory.GetFiles(logDir, "stderr-*.log", SearchOption.TopDirectoryOnly).Single();
            var log = File.ReadAllText(logPath);
            Assert.Contains("session_start", log);
            Assert.Contains("args=definitely-not-a-command", log);
            Assert.Contains("Unknown command: definitely-not-a-command", log);
            Assert.Contains("command_complete exit_code=1", log);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG", originalForce);
            Environment.SetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG", originalDisable);
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", originalLogDir);
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_PrunesToThirtyDailyFiles()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_prune_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var originalForce = Environment.GetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG");
        var originalDisable = Environment.GetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG");
        var originalLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            for (var i = 0; i < 35; i++)
            {
                var date = new DateTime(2024, 1, 1).AddDays(i);
                File.WriteAllText(Path.Combine(logDir, $"stderr-{date:yyyyMMdd}.log"), $"old {i}");
            }

            Environment.SetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            Environment.SetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG", null);
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

            var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Unknown command: definitely-not-a-command", stderr);

            var logs = Directory.GetFiles(logDir, "stderr-*.log", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(30, logs.Count);
            Assert.DoesNotContain("stderr-20240101.log", logs);
            Assert.DoesNotContain("stderr-20240105.log", logs);
            Assert.Contains($"stderr-{DateTime.UtcNow:yyyyMMdd}.log", logs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG", originalForce);
            Environment.SetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG", originalDisable);
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", originalLogDir);
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_CanBeDisabledExplicitly()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_disabled_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var originalForce = Environment.GetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG");
        var originalDisable = Environment.GetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG");
        var originalLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            Environment.SetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            Environment.SetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG", "1");
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("Unknown command: definitely-not-a-command", stderr);
            Assert.Empty(Directory.GetFiles(logDir, "stderr-*.log", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_FORCE_GLOBAL_TOOL_LOG", originalForce);
            Environment.SetEnvironmentVariable("CDIDX_DISABLE_PERSISTENT_LOG", originalDisable);
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", originalLogDir);
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_StatusJson_UsesSourceGeneratedSerializerWhenReflectionResolverFails()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("program_runner_json_status");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/App.cs", "csharp", "class App {}\n");

            var options = CreateTrimmedFailureJsonOptions();
            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["status", "--db", dbPath, "--json"],
                options,
                "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(1, document.RootElement.GetProperty("files").GetInt32());
            Assert.Equal("1.10.0", document.RootElement.GetProperty("version").GetString());
            Assert.Equal(string.Empty, stderr);
            Assert.DoesNotContain("database error", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IndexJson_UsesSourceGeneratedSerializerWhenReflectionResolverFails()
    {
        var missingProject = Path.Combine(Path.GetTempPath(), $"program_runner_missing_{Guid.NewGuid():N}");
        var options = CreateTrimmedFailureJsonOptions();

        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            [missingProject, "--json"],
            options,
            "1.10.0"));

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Contains("directory not found", document.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain("directory not found", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(new[] { "--color=always", "status" }, ColorMode.Always, new[] { "status" })]
    [InlineData(new[] { "status", "--color", "never" }, ColorMode.Never, new[] { "status" })]
    [InlineData(new[] { "search", "--color=auto", "foo" }, ColorMode.Auto, new[] { "search", "foo" })]
    [InlineData(new[] { "status" }, ColorMode.Auto, new[] { "status" })]
    public void TryConsumeColorFlag_StripsFlagAndSetsMode(string[] input, ColorMode expectedMode, string[] expectedKept)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalMode = ConsoleUi.GetColorMode();
            try
            {
                var args = input;
                Assert.True(ProgramRunner.TryConsumeColorFlag(ref args, out var error));
                Assert.Empty(error);
                Assert.Equal(expectedMode, ConsoleUi.GetColorMode());
                Assert.Equal(expectedKept, args);
            }
            finally
            {
                ConsoleUi.SetColorMode(originalMode);
            }
        }
    }

    [Fact]
    public void TryConsumeColorFlag_InvalidValue_ReturnsError()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalMode = ConsoleUi.GetColorMode();
            try
            {
                var args = new[] { "search", "--color=sparkly" };
                Assert.False(ProgramRunner.TryConsumeColorFlag(ref args, out var error));
                Assert.Contains("sparkly", error);
            }
            finally
            {
                ConsoleUi.SetColorMode(originalMode);
            }
        }
    }

    [Fact]
    public void TryConsumeColorFlag_MissingValue_ReturnsError()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalMode = ConsoleUi.GetColorMode();
            try
            {
                var args = new[] { "search", "--color" };
                Assert.False(ProgramRunner.TryConsumeColorFlag(ref args, out var error));
                Assert.Contains("requires a value", error);
            }
            finally
            {
                ConsoleUi.SetColorMode(originalMode);
            }
        }
    }

    [Fact]
    public void TryConsumeColorFlag_AfterDoubleDash_PreservesQueryEscape()
    {
        // `--` is the query-escape sentinel; anything after it must be left in
        // place so subcommands like `cdidx search -- --color=auto` can treat
        // `--color=auto` as a literal query argument rather than the global flag.
        lock (TestConsoleLock.Gate)
        {
            var originalMode = ConsoleUi.GetColorMode();
            try
            {
                var args = new[] { "search", "--", "--color=auto" };
                Assert.True(ProgramRunner.TryConsumeColorFlag(ref args, out var error));
                Assert.Empty(error);
                Assert.Equal(ColorMode.Auto, ConsoleUi.GetColorMode());
                Assert.Equal(new[] { "search", "--", "--color=auto" }, args);
            }
            finally
            {
                ConsoleUi.SetColorMode(originalMode);
            }
        }
    }

    [Fact]
    public void TryConsumeColorFlag_FlagBeforeDoubleDash_StillConsumed()
    {
        // The global flag must still be consumed when it appears before `--`,
        // even if the same string appears again afterward as a literal query.
        lock (TestConsoleLock.Gate)
        {
            var originalMode = ConsoleUi.GetColorMode();
            try
            {
                var args = new[] { "search", "--color=always", "--", "--color=auto" };
                Assert.True(ProgramRunner.TryConsumeColorFlag(ref args, out var error));
                Assert.Empty(error);
                Assert.Equal(ColorMode.Always, ConsoleUi.GetColorMode());
                Assert.Equal(new[] { "search", "--", "--color=auto" }, args);
            }
            finally
            {
                ConsoleUi.SetColorMode(originalMode);
            }
        }
    }

    [Fact]
    public void TryConsumeAsciiFlag_StripsFlagBeforeDoubleDashAndForcesAscii()
    {
        lock (TestConsoleLock.Gate)
        {
            var original = ConsoleUi.IsAsciiOutputForced();
            try
            {
                var args = new[] { "index", "--ascii", "--", "--ascii" };
                ProgramRunner.TryConsumeAsciiFlag(ref args);

                Assert.True(ConsoleUi.IsAsciiOutputForced());
                Assert.Equal(new[] { "index", "--", "--ascii" }, args);
            }
            finally
            {
                ConsoleUi.SetAsciiOutput(original);
            }
        }
    }

    [Fact]
    public void Run_InvalidColorValue_ReturnsInvalidArgument()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["--color=sparkly", "status"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("invalid --color value `sparkly`", stderr);
        Assert.Contains("Hint:", stderr);
    }

    [Theory]
    [InlineData(new[] { "--palette=truecolor", "status" }, ColorPalette.Truecolor, new[] { "status" })]
    [InlineData(new[] { "status", "--palette", "256" }, ColorPalette.Color256, new[] { "status" })]
    [InlineData(new[] { "search", "--palette=basic", "foo" }, ColorPalette.Basic, new[] { "search", "foo" })]
    public void TryConsumePaletteFlag_StripsFlagAndSetsPalette(string[] input, ColorPalette expected, string[] expectedKept)
    {
        lock (TestConsoleLock.Gate)
        {
            var original = ConsoleUi.GetExplicitColorPalette();
            try
            {
                var args = input;
                Assert.True(ProgramRunner.TryConsumePaletteFlag(ref args, out var error));
                Assert.Empty(error);
                Assert.Equal(expected, ConsoleUi.GetExplicitColorPalette());
                Assert.Equal(expectedKept, args);
            }
            finally
            {
                ConsoleUi.SetColorPalette(original);
            }
        }
    }

    [Fact]
    public void TryConsumePaletteFlag_NoFlag_ClearsExplicitOverride()
    {
        lock (TestConsoleLock.Gate)
        {
            var original = ConsoleUi.GetExplicitColorPalette();
            try
            {
                ConsoleUi.SetColorPalette(ColorPalette.Truecolor);
                var args = new[] { "status" };
                Assert.True(ProgramRunner.TryConsumePaletteFlag(ref args, out var error));
                Assert.Empty(error);
                Assert.Null(ConsoleUi.GetExplicitColorPalette());
                Assert.Equal(new[] { "status" }, args);
            }
            finally
            {
                ConsoleUi.SetColorPalette(original);
            }
        }
    }

    [Fact]
    public void TryConsumePaletteFlag_InvalidValue_ReturnsError()
    {
        lock (TestConsoleLock.Gate)
        {
            var original = ConsoleUi.GetExplicitColorPalette();
            try
            {
                var args = new[] { "search", "--palette=fancy" };
                Assert.False(ProgramRunner.TryConsumePaletteFlag(ref args, out var error));
                Assert.Contains("fancy", error);
            }
            finally
            {
                ConsoleUi.SetColorPalette(original);
            }
        }
    }

    [Fact]
    public void TryConsumePaletteFlag_MissingValue_ReturnsError()
    {
        lock (TestConsoleLock.Gate)
        {
            var original = ConsoleUi.GetExplicitColorPalette();
            try
            {
                var args = new[] { "search", "--palette" };
                Assert.False(ProgramRunner.TryConsumePaletteFlag(ref args, out var error));
                Assert.Contains("requires a value", error);
            }
            finally
            {
                ConsoleUi.SetColorPalette(original);
            }
        }
    }

    [Fact]
    public void TryConsumePaletteFlag_AfterDoubleDash_PreservesQueryEscape()
    {
        lock (TestConsoleLock.Gate)
        {
            var original = ConsoleUi.GetExplicitColorPalette();
            try
            {
                var args = new[] { "search", "--", "--palette=truecolor" };
                Assert.True(ProgramRunner.TryConsumePaletteFlag(ref args, out var error));
                Assert.Empty(error);
                Assert.Null(ConsoleUi.GetExplicitColorPalette());
                Assert.Equal(new[] { "search", "--", "--palette=truecolor" }, args);
            }
            finally
            {
                ConsoleUi.SetColorPalette(original);
            }
        }
    }

    [Fact]
    public void Run_InvalidPaletteValue_ReturnsInvalidArgument()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["--palette=fancy", "status"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("invalid --palette value `fancy`", stderr);
        Assert.Contains("Hint:", stderr);
    }

    [Fact]
    public void Run_Version_HumanOutput_IncludesBuildMetadata()
    {
        // Issue #1550: `cdidx --version` should distinguish dev builds from
        // tagged releases by appending a parenthesised `(commit <sha>, built
        // <date>, <clean|dirty>)` suffix. The exact values come from MSBuild
        // stamping so we assert on the structural shape only.
        // #1550: --version 出力で開発ビルドとリリースを区別できるよう、コミット SHA /
        // ビルド日 / clean|dirty 情報を末尾に付与する。値は MSBuild が刻印するため
        // ここでは構造のみを検証する。
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["--version"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        var line = stdout.Trim();
        Assert.StartsWith("cdidx v", line);
        // Either bare `cdidx v<ver>` (no metadata stamped) or with full suffix.
        // メタ刻印が無いビルドでは `cdidx v<ver>` のみ、ある場合は括弧付き末尾。
        if (line.Contains('('))
        {
            Assert.Contains("commit ", line);
            Assert.Contains(", built ", line);
            Assert.EndsWith(")", line);
        }
    }

    [Fact]
    public void Run_Version_JsonOutput_HasExpectedShape()
    {
        // Issue #1550: `cdidx --version --json` is the machine-readable form
        // used by support tooling. All five keys must be present.
        // #1550: ツール連携用の --version --json 出力。5 キーが揃うことを検証。
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["--version", "--json"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.Equal("cdidx", root.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("commit").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("build_date").GetString()));
        var dirty = root.GetProperty("dirty").GetString();
        Assert.Contains(dirty, new[] { "clean", "dirty", "unknown" });
    }

    [Fact]
    public void Run_Version_UnknownFlag_ReturnsUsageError()
    {
        // Stray tokens after --version are a typo (`--Json`, `-v`) rather than
        // a valid mode and should fail loudly with a hint, not be silently
        // ignored.
        // --version の後ろの未知フラグは打ち間違いとみなしてヒント付きで失敗させる。
        var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["--version", "--bogus"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--version does not accept '--bogus'", stderr);
        Assert.Contains("Hint:", stderr);
    }

    [Fact]
    public void IsTrimmedJsonUnavailable_RecognizesReflectionDisabledMessage()
    {
        var ex = new InvalidOperationException(JsonOutputFailure.ReflectionDisabledMessage);

        Assert.True(JsonOutputFailure.IsTrimmedJsonUnavailable(ex));
    }

    [Fact]
    public void TryConsumeDebugUnsafeFlag_StripsFlagAndEnablesProcessGate()
    {
        // Issue #1530: `--debug-unsafe` is the explicit per-process opt-in
        // required for CDIDX_DEBUG=unsafe to actually emit raw text. The flag
        // must be consumed so it never reaches the subcommand parser.
        lock (TestConsoleLock.Gate)
        {
            DbDebug.ResetForTesting();
            try
            {
                var args = new[] { "search", "--debug-unsafe", "foo" };
                Assert.True(ProgramRunner.TryConsumeDebugUnsafeFlag(ref args));
                Assert.Equal(new[] { "search", "foo" }, args);
                Assert.True(DbDebug.IsUnsafeAllowedForProcess());
            }
            finally
            {
                DbDebug.ResetForTesting();
            }
        }
    }

    [Fact]
    public void TryConsumeDebugUnsafeFlag_AbsentFlag_LeavesGateClosed()
    {
        lock (TestConsoleLock.Gate)
        {
            DbDebug.ResetForTesting();
            try
            {
                var args = new[] { "search", "foo" };
                Assert.False(ProgramRunner.TryConsumeDebugUnsafeFlag(ref args));
                Assert.Equal(new[] { "search", "foo" }, args);
                Assert.False(DbDebug.IsUnsafeAllowedForProcess());
            }
            finally
            {
                DbDebug.ResetForTesting();
            }
        }
    }

    [Fact]
    public void TryConsumeDebugUnsafeFlag_AfterDoubleDash_PreservesQueryEscape()
    {
        // `--` is the query-escape sentinel for subcommands; tokens after it
        // must stay literal even if they collide with global flag names.
        lock (TestConsoleLock.Gate)
        {
            DbDebug.ResetForTesting();
            try
            {
                var args = new[] { "search", "--", "--debug-unsafe" };
                Assert.False(ProgramRunner.TryConsumeDebugUnsafeFlag(ref args));
                Assert.Equal(new[] { "search", "--", "--debug-unsafe" }, args);
                Assert.False(DbDebug.IsUnsafeAllowedForProcess());
            }
            finally
            {
                DbDebug.ResetForTesting();
            }
        }
    }

    private static JsonSerializerOptions CreateTrimmedFailureJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new ThrowingResolver(),
    };

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

    private sealed class ThrowingResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            throw new InvalidOperationException(JsonOutputFailure.ReflectionDisabledMessage);
    }

    // --- --audit-log flag parsing (#1562) ---

    [Fact]
    public void TryConsumeAuditLogFlags_NoFlags_LeavesArgsUntouched()
    {
        var args = new[] { "--db", "/tmp/x.db", "--", "foo" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out var options, out var error);

        Assert.True(ok);
        Assert.Equal(string.Empty, error);
        Assert.Null(options.Path);
        Assert.False(options.IncludeValues);
        Assert.Equal(AuditLogSink.DefaultMaxBytes, options.MaxBytes);
        Assert.Equal(new[] { "--db", "/tmp/x.db", "--", "foo" }, args);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_SpaceSeparatedPath_StrippedFromArgs()
    {
        var args = new[] { "--audit-log", "/tmp/audit.jsonl", "--db", "/tmp/x.db" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out var options, out _);

        Assert.True(ok);
        Assert.Equal("/tmp/audit.jsonl", options.Path);
        Assert.Equal(new[] { "--db", "/tmp/x.db" }, args);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_EqualsSeparatedPath_StrippedFromArgs()
    {
        var args = new[] { "--audit-log=/tmp/audit.jsonl", "--db", "/tmp/x.db" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out var options, out _);

        Assert.True(ok);
        Assert.Equal("/tmp/audit.jsonl", options.Path);
        Assert.Equal(new[] { "--db", "/tmp/x.db" }, args);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_IncludeValues_StrippedFromArgs()
    {
        var args = new[] { "--audit-log", "/tmp/a.jsonl", "--audit-log-include-values" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out var options, out _);

        Assert.True(ok);
        Assert.True(options.IncludeValues);
        Assert.Empty(args);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_MaxBytes_SpaceAndEqualsForms()
    {
        var argsA = new[] { "--audit-log", "/tmp/a.jsonl", "--audit-log-max-bytes", "8192" };
        Assert.True(ProgramRunner.TryConsumeAuditLogFlags(ref argsA, out var optionsA, out _));
        Assert.Equal(8192, optionsA.MaxBytes);

        var argsB = new[] { "--audit-log", "/tmp/a.jsonl", "--audit-log-max-bytes=16384" };
        Assert.True(ProgramRunner.TryConsumeAuditLogFlags(ref argsB, out var optionsB, out _));
        Assert.Equal(16384, optionsB.MaxBytes);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_MissingPath_ReturnsError()
    {
        var args = new[] { "--audit-log" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out _, out var error);

        Assert.False(ok);
        Assert.Contains("--audit-log requires a path", error);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_EmptyEqualsPath_ReturnsError()
    {
        var args = new[] { "--audit-log=" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out _, out var error);

        Assert.False(ok);
        Assert.Contains("non-empty path", error);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_IncludeValuesWithoutPath_ReturnsError()
    {
        var args = new[] { "--audit-log-include-values" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out _, out var error);

        Assert.False(ok);
        Assert.Contains("--audit-log-include-values requires --audit-log", error);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_MaxBytesBelowMin_ReturnsError()
    {
        var args = new[] { "--audit-log", "/tmp/a.jsonl", "--audit-log-max-bytes", "10" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out _, out var error);

        Assert.False(ok);
        Assert.Contains("--audit-log-max-bytes must be an integer", error);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_NonNumericMaxBytes_ReturnsError()
    {
        var args = new[] { "--audit-log", "/tmp/a.jsonl", "--audit-log-max-bytes=oops" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out _, out var error);

        Assert.False(ok);
        Assert.Contains("--audit-log-max-bytes must be an integer", error);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_PassthroughAfterDoubleDash_PreservesAuditTokens()
    {
        // Anything after `--` belongs to the wrapped command and must be left alone.
        // `--` 以降は後続コマンドに渡るのでパース対象から外す。
        var args = new[] { "--audit-log", "/tmp/a.jsonl", "--", "--audit-log-include-values" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out var options, out _);

        Assert.True(ok);
        Assert.False(options.IncludeValues);
        Assert.Equal(new[] { "--", "--audit-log-include-values" }, args);
    }

    [Fact]
    public void TryConsumeAuditLogFlags_DbValueLooksLikeAuditFlag_PreservedAsDbValue()
    {
        // Regression for #1562 codex review: `--db <value>` may carry a dash-prefixed
        // URI/path that happens to share a prefix with an audit flag. The pre-parser
        // must hand the value through to the strict mcp parser instead of consuming it
        // as the start of `--audit-log`.
        // #1562 codex レビュー回帰: `--db --audit-log` などダッシュ始まりの DB 値を
        // audit-log フラグの先頭と誤認して取り込まないこと。
        var args = new[] { "--db", "--audit-log" };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out var options, out var error);

        Assert.True(ok, $"expected success but got error: {error}");
        Assert.Null(options.Path);
        Assert.Equal(new[] { "--db", "--audit-log" }, args);
    }
}
