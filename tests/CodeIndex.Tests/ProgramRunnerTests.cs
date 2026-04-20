using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class ProgramRunnerTests
{
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
    public void Run_StatusJsonTrimFailure_ReturnsFeatureUnavailableInsteadOfDatabaseError()
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

            Assert.Equal(CommandExitCodes.FeatureUnavailable, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("--json is not available on this trimmed build", stderr);
            Assert.Contains("use `cdidx mcp` for structured output", stderr);
            Assert.DoesNotContain("database error", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_IndexJsonTrimFailure_ReturnsFeatureUnavailable()
    {
        var missingProject = Path.Combine(Path.GetTempPath(), $"program_runner_missing_{Guid.NewGuid():N}");
        var options = CreateTrimmedFailureJsonOptions();

        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            [missingProject, "--json"],
            options,
            "1.10.0"));

        Assert.Equal(CommandExitCodes.FeatureUnavailable, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--json is not available on this trimmed build", stderr);
        Assert.DoesNotContain("directory not found", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsTrimmedJsonUnavailable_RecognizesReflectionDisabledMessage()
    {
        var ex = new InvalidOperationException(JsonOutputFailure.ReflectionDisabledMessage);

        Assert.True(JsonOutputFailure.IsTrimmedJsonUnavailable(ex));
    }

    private static JsonSerializerOptions CreateTrimmedFailureJsonOptions() => new()
    {
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
}
