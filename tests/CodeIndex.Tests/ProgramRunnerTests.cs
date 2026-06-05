using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class ProgramRunnerTests
{
    [Theory]
    [InlineData("foo.cs", false)]
    [InlineData("./foo", true)]
    [InlineData(".", true)]
    public void IsProjectPathArg_CommonForms_ReturnsExpectedValue(string arg, bool expected)
    {
        Assert.Equal(expected, ProgramRunner.IsProjectPathArg(arg));
    }

    [Fact]
    public void IsProjectPathArg_PosixLiteralBackslashFileName_IsNotPathSyntax()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.False(ProgramRunner.IsProjectPathArg(@"weird\name.txt"));
    }

    [Theory]
    [InlineData(@"C:\foo")]
    [InlineData("C:")]
    [InlineData(@"\\server\share\foo")]
    public void IsProjectPathArg_WindowsPathForms_ReturnTrueOnWindows(string arg)
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(ProgramRunner.IsProjectPathArg(arg));
    }

    [Fact]
    public void ResolveMcpHttpBearerTokenFromEnvironment_HttpTokenWinsThenFallsBackToGeneric()
    {
        using var env = EnvironmentVariableScope.Capture(
            ProgramRunner.McpHttpTokenEnvVar,
            McpAuthenticatorFactory.AuthTokenEnvVar);

        env.Set(ProgramRunner.McpHttpTokenEnvVar, "http-secret");
        env.Set(McpAuthenticatorFactory.AuthTokenEnvVar, "generic-secret");
        Assert.Equal("http-secret", ProgramRunner.ResolveMcpHttpBearerTokenFromEnvironment());

        env.Set(ProgramRunner.McpHttpTokenEnvVar, "   ");
        Assert.Equal("generic-secret", ProgramRunner.ResolveMcpHttpBearerTokenFromEnvironment());

        env.Set(McpAuthenticatorFactory.AuthTokenEnvVar, "\t");
        Assert.Null(ProgramRunner.ResolveMcpHttpBearerTokenFromEnvironment());
    }

    [Fact]
    public void CreateMcpAuthenticatorForTransport_HttpUsesBearerGateInsteadOfBodyTokenGate()
    {
        using var env = EnvironmentVariableScope.Capture(McpAuthenticatorFactory.AuthTokenEnvVar);
        env.Set(McpAuthenticatorFactory.AuthTokenEnvVar, "generic-secret");

        Assert.IsType<TokenMcpAuthenticator>(ProgramRunner.CreateMcpAuthenticatorForTransport("stdio"));
        Assert.IsType<LocalStdioAuthenticator>(ProgramRunner.CreateMcpAuthenticatorForTransport("http"));
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--json=array")]
    [InlineData("--json-envelope")]
    public void ContainsJsonOutputFlag_JsonModes_ReturnsTrue(string jsonFlag)
    {
        Assert.True(ProgramRunner.ContainsJsonOutputFlag(["search", "Needle", jsonFlag]));
    }

    [Fact]
    public void ContainsJsonOutputFlag_AfterPassthrough_ReturnsFalse()
    {
        Assert.False(ProgramRunner.ContainsJsonOutputFlag(["search", "--", "--json"]));
    }

    [Fact]
    public void RunLanguages_PrettyJson_IndentsOutput_Issue2996()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["languages", "--json", "--pretty"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains(Environment.NewLine + "  \"languages\": [", stdout);
        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.TryGetProperty("languages", out _));
    }

    [Fact]
    public void RunSearch_FirstQueryLiteralMatchingPrettyFlag_IsNotConsumed_Issue2996()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_pretty_query_literal");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                "--pretty appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "--pretty", "--db", dbPath, "--path", "README.md", "--count", "--exact-substring"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1" + Environment.NewLine, stdout);
            Assert.Empty(stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_NdjsonWithPretty_KeepsOneJsonValuePerLine_Issue2996()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_pretty_ndjson");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                "class App { void Needle() {} }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Needle", "--db", dbPath, "--json", "--pretty"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            var lines = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            foreach (var line in lines)
            {
                Assert.False(line.StartsWith(" ", StringComparison.Ordinal));
                using var _ = JsonDocument.Parse(line);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_TestExtractor_PrintsIsolatedSymbols()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_test_extractor_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);
                var file = Path.Combine(tempDir, "app.py");
                File.WriteAllText(file, "def hello():\n    pass\n");

                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["test-extractor", "--language", "python", "--file", file, "--json"],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stderr);
                using var document = JsonDocument.Parse(stdout);
                Assert.Contains(document.RootElement.EnumerateArray(), item =>
                    item.GetProperty("Kind").GetString() == "function"
                    && item.GetProperty("Name").GetString() == "hello");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void Run_TestExtractor_SourceTooLarge_ReturnsInvalidArgument_Issue2896()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_test_extractor_large_source_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);
                var file = Path.Combine(tempDir, "large.py");
                File.WriteAllText(file, new string('x', (int)ProgramRunner.TestExtractorMaxInputBytes + 1));

                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["test-extractor", "--language", "python", "--file", file, "--json"],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
                Assert.Empty(stdout);
                Assert.Contains("test-extractor source file is too large", stderr);
                Assert.Contains($"{ProgramRunner.TestExtractorMaxInputBytes} byte limit", stderr);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(tempDir);
            }
        }
    }

    [Fact]
    public void Run_TestExtractor_ExpectedSymbolsTooLarge_ReturnsInvalidArgument_Issue2896()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_test_extractor_large_expect_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);
                var file = Path.Combine(tempDir, "app.py");
                var expect = Path.Combine(tempDir, "expected.json");
                File.WriteAllText(file, "def hello():\n    pass\n");
                File.WriteAllText(expect, new string('x', (int)ProgramRunner.TestExtractorMaxInputBytes + 1));

                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["test-extractor", "--language", "python", "--file", file, "--expect-symbols", expect],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
                Assert.Empty(stdout);
                Assert.Contains("test-extractor expected symbols file is too large", stderr);
                Assert.Contains($"{ProgramRunner.TestExtractorMaxInputBytes} byte limit", stderr);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(tempDir);
            }
        }
    }

    [Fact]
    public void TryConsumeQueryTraceFlag_StripsTraceAndPreservesEscapedQuery()
    {
        string[] args = ["needle", "--trace=stderr", "--lang", "csharp", "--", "--trace=file"];

        var ok = ProgramRunner.TryConsumeQueryTraceFlag(ref args, out var mode, out var error);

        Assert.True(ok);
        Assert.Empty(error);
        Assert.Equal("stderr", mode);
        Assert.Equal(["needle", "--lang", "csharp", "--", "--trace=file"], args);
    }

    [Fact]
    public void Run_QueryTraceStderr_EmitsStructuredSanitizedLine()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("query-trace");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void Needle() { } }");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Needle", "--db", dbPath, "--trace=stderr", "--count", "--lang", "csharp", "--limit", "7", "--path", "src/**"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            var traceLine = stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Single(line => line.StartsWith('{'));
            using var document = JsonDocument.Parse(traceLine);
            var root = document.RootElement;
            Assert.Equal("search", root.GetProperty("tool").GetString());
            Assert.Equal("cli_query", root.GetProperty("source").GetString());
            Assert.Equal(1, root.GetProperty("result_count").GetInt32());
            Assert.Equal(0, root.GetProperty("exit_code").GetInt32());
            Assert.Equal("csharp", root.GetProperty("parameters").GetProperty("lang").GetString());
            Assert.Equal("7", root.GetProperty("parameters").GetProperty("limit").GetString());
            Assert.Contains("src/**", root.GetProperty("parameters").GetProperty("path")[0].GetString());
            Assert.DoesNotContain("Needle", traceLine);
            Assert.DoesNotContain(dbPath, traceLine);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_QueryTraceFile_AppendsDailyJsonl()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("query-trace-file");
        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_query_trace_{Guid.NewGuid():N}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void Needle() { } }");
            using var env = EnvironmentVariableScope.Capture("CDIDX_GLOBAL_TOOL_LOG_DIR");
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Needle", "--db", dbPath, "--trace=file"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.DoesNotContain('{', stderr);
            var tracePath = Path.Combine(logRoot, $"query-trace-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            Assert.True(File.Exists(tracePath));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(PrivateLogFile.PrivateFileMode, File.GetUnixFileMode(tracePath));
            var line = File.ReadAllLines(tracePath).Single();
            using var document = JsonDocument.Parse(line);
            Assert.Equal("search", document.RootElement.GetProperty("tool").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            if (Directory.Exists(logRoot))
                Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public void Run_QueryTraceFile_PrunesToThirtyTraceFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("query-trace-prune");
        var logRoot = Path.Combine(Path.GetTempPath(), $"cdidx_query_trace_prune_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(logRoot);
            for (var i = 0; i < 35; i++)
            {
                var date = new DateTime(2024, 1, 1).AddDays(i);
                var path = Path.Combine(logRoot, $"query-trace-{date:yyyyMMdd}.jsonl");
                File.WriteAllText(path, $"old {i}");
                File.SetLastWriteTimeUtc(path, date);
            }

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void Needle() { } }");
            using var env = EnvironmentVariableScope.Capture("CDIDX_GLOBAL_TOOL_LOG_DIR");
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logRoot);

            var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Needle", "--db", dbPath, "--trace=file"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.DoesNotContain('{', stderr);

            var traces = Directory.GetFiles(logRoot, "query-trace-*.jsonl", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(30, traces.Length);
            Assert.DoesNotContain("query-trace-20240101.jsonl", traces);
            Assert.Contains($"query-trace-{DateTime.UtcNow:yyyyMMdd}.jsonl", traces);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            if (Directory.Exists(logRoot))
                Directory.Delete(logRoot, recursive: true);
        }
    }

    [Fact]
    public void TryConsumeSuggestionDedupThresholdFlag_SetsEnvironmentAndRemovesFlag()
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.DedupThresholdEnvironmentVariable);
        env.Set(SuggestionStore.DedupThresholdEnvironmentVariable, null);
        string[] args = ["--db", "index.db", "--suggestion-dedup-threshold", "0.7"];

        var ok = ProgramRunner.TryConsumeSuggestionDedupThresholdFlag(ref args, out var error);

        Assert.True(ok);
        Assert.Empty(error);
        Assert.Equal(["--db", "index.db"], args);
        Assert.Equal("0.7", Environment.GetEnvironmentVariable(SuggestionStore.DedupThresholdEnvironmentVariable));
    }

    [Fact]
    public void TryConsumeSuggestionDedupThresholdFlag_InvalidValue_ReturnsError()
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.DedupThresholdEnvironmentVariable);
        env.Set(SuggestionStore.DedupThresholdEnvironmentVariable, null);
        string[] args = ["--suggestion-dedup-threshold=1.5"];

        var ok = ProgramRunner.TryConsumeSuggestionDedupThresholdFlag(ref args, out var error);

        Assert.False(ok);
        Assert.Contains("--suggestion-dedup-threshold", error);
        Assert.Null(Environment.GetEnvironmentVariable(SuggestionStore.DedupThresholdEnvironmentVariable));
    }

    [Fact]
    public void Run_UnhandledException_ReturnsSanitizedSingleLineError()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["status"],
            appVersion: "1.10.0",
            beforeDispatchForTesting: () => throw new InvalidOperationException("boom")));

        Assert.Equal(CommandExitCodes.UnhandledException, exitCode);
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
    public void Run_WorkspaceVersionPinMismatch_WarnsByDefault()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("version-pin-warn");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, ".cdidx-version"), "9.9.9\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["--version", "--json"],
                appVersion: "1.10.0",
                configStartDirectory: projectRoot));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("\"version\":\"1.10.0\"", stdout);
            Assert.Contains("workspace requires cdidx v9.9.9", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WorkspaceVersionPinMismatch_StrictFailsBeforeCommand()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("version-pin-strict");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, ".cdidx-version"), "9.9.9\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["--strict-version", "--version"],
                appVersion: "1.10.0",
                configStartDirectory: projectRoot));

            Assert.Equal(CommandExitCodes.ExUsage, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("workspace requires cdidx v9.9.9", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WorkspaceVersionPinUtf8Bom_MatchingStrictPinSucceeds()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("version-pin-bom");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, ".cdidx-version"),
                "1.10.0\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["--strict-version", "--version", "--json"],
                appVersion: "1.10.0",
                configStartDirectory: projectRoot));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("\"version\":\"1.10.0\"", stdout);
            Assert.DoesNotContain("workspace requires cdidx", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WorkspaceVersionPinTooLarge_WarnsAndIgnoresPin()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("version-pin-large");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, ".cdidx-version"),
                "9.9.9\n" + new string('x', ProgramRunner.WorkspaceVersionPinMaxBytes));

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["--strict-version", "--version", "--json"],
                appVersion: "1.10.0",
                configStartDirectory: projectRoot));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("\"version\":\"1.10.0\"", stdout);
            Assert.Contains($"file exceeds {ProgramRunner.WorkspaceVersionPinMaxBytes} bytes", stderr);
            Assert.DoesNotContain("workspace requires cdidx v9.9.9", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WorkspaceVersionPinTooManyBlankLines_WarnsAndIgnoresPin()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("version-pin-blanks");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, ".cdidx-version"),
                new string('\n', ProgramRunner.WorkspaceVersionPinMaxSkippedBlankLines + 1) + "9.9.9\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["--strict-version", "--version", "--json"],
                appVersion: "1.10.0",
                configStartDirectory: projectRoot));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("\"version\":\"1.10.0\"", stdout);
            Assert.Contains($"more than {ProgramRunner.WorkspaceVersionPinMaxSkippedBlankLines} leading blank lines", stderr);
            Assert.DoesNotContain("workspace requires cdidx v9.9.9", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_WorkspaceVersionPinLineTooLong_WarnsAndIgnoresPin()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("version-pin-line");
        try
        {
            File.WriteAllText(
                Path.Combine(projectRoot, ".cdidx-version"),
                new string('9', ProgramRunner.WorkspaceVersionPinMaxLineChars + 1) + "\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["--strict-version", "--version", "--json"],
                appVersion: "1.10.0",
                configStartDirectory: projectRoot));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("\"version\":\"1.10.0\"", stdout);
            Assert.Contains($"line 1 exceeds {ProgramRunner.WorkspaceVersionPinMaxLineChars} characters", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void UpdateChecker_Check_ReportsNewerRelease()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        try
        {
            var result = UpdateChecker.Check(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                _ => Task.FromResult<string?>("v1.11.0"));

            Assert.True(result.UpdateAvailable);
            Assert.Equal("v1.11.0", result.LatestVersion);
            Assert.False(result.FromCache);
        }
        finally
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_IgnoresOversizedCache()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(cachePath, new string('x', UpdateChecker.MaxUpdateCheckCacheBytes + 1));

            var result = UpdateChecker.Check(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                _ => Task.FromResult<string?>("v1.11.0"));

            Assert.False(result.FromCache);
            Assert.Equal("v1.11.0", result.LatestVersion);
            Assert.True(result.UpdateAvailable);
        }
        finally
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_PassesCallerCancellationTokenToFetch()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        using var cts = new CancellationTokenSource();
        CancellationToken observedToken = default;
        try
        {
            var result = UpdateChecker.Check(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                token =>
                {
                    observedToken = token;
                    return Task.FromResult<string?>("v1.11.0");
                },
                cts.Token);

            Assert.Equal(cts.Token, observedToken);
            Assert.True(result.UpdateAvailable);
        }
        finally
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_PropagatesCallerCancellation()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                UpdateChecker.Check(
                    "1.10.0",
                    cachePath,
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    token => throw new OperationCanceledException(token),
                    cts.Token));
        }
        finally
        {
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
    }

    [Theory]
    [InlineData("v1.26.0", "https://raw.githubusercontent.com/Widthdom/CodeIndex/v1.26.0/install.sh")]
    [InlineData(" release/test ", "https://raw.githubusercontent.com/Widthdom/CodeIndex/release%2Ftest/install.sh")]
    public void BuildInstallerScriptUrl_UsesResolvedReleaseTag(string releaseTag, string expected)
    {
        Assert.Equal(expected, ProgramRunner.BuildInstallerScriptUrl(releaseTag));
    }

    [Fact]
    public void CreateInstallerProcessStartInfo_UsesArgumentList()
    {
        var startInfo = ProgramRunner.CreateInstallerProcessStartInfo(
            "/tmp/install script's path.sh",
            "v1.27.0",
            "/opt/cdidx install");

        Assert.Equal("bash", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(["/tmp/install script's path.sh", "v1.27.0"], startInfo.ArgumentList.ToArray());
        Assert.Equal("/opt/cdidx install", startInfo.Environment["CDIDX_INSTALL_DIR"]);
    }

    [Fact]
    public void RunInstallerProcess_TimesOutHungInstaller()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cdidx_installer_timeout_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var script = Path.Combine(root, "install.sh");
            try
            {
                File.WriteAllText(script, """
#!/bin/sh
sleep 5
""");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var startInfo = ProgramRunner.CreateInstallerProcessStartInfo(script, "v1.27.0", root);

                var (exitCode, stdout, stderr) = CaptureConsole(() =>
                    ProgramRunner.RunInstallerProcess(startInfo, TimeSpan.FromMilliseconds(100)));

                Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
                Assert.Empty(stdout);
                Assert.Contains("install.sh timed out", stderr);
                Assert.Contains("rerun `install.sh` manually", stderr);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(root);
            }
        }
    }

    [Fact]
    public async Task DownloadInstallerScriptAsync_CancelsStalledBody()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx-install-timeout-{Guid.NewGuid():N}.sh");
        using var client = new HttpClient(new StaticResponseHandler(new StalledContent()))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ProgramRunner.DownloadInstallerScriptAsync(
                    client,
                    "v1.27.0",
                    path,
                    TimeSpan.FromMilliseconds(25),
                    CancellationToken.None));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateChecker_ReadLatestReleaseTagAsync_ParsesTagName()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"tag_name":"v1.27.0"}"""));

        var tag = await UpdateChecker.ReadLatestReleaseTagAsync(content, CancellationToken.None);

        Assert.Equal("v1.27.0", tag);
    }

    [Fact]
    public async Task UpdateChecker_ReadLatestReleaseTagAsync_RejectsOverLimitResponse()
    {
        using var content = new ByteArrayContent(new byte[(int)UpdateChecker.MaxLatestReleaseResponseBytes + 1]);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpdateChecker.ReadLatestReleaseTagAsync(content, CancellationToken.None));

        Assert.Contains($"{UpdateChecker.MaxLatestReleaseResponseBytes} byte limit", ex.Message);
    }

    [Fact]
    public async Task UpdateChecker_ReadLatestReleaseTagAsync_RejectsDeepJson()
    {
        var depth = UpdateChecker.MaxLatestReleaseJsonDepth + 8;
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('[', depth) + new string(']', depth)));

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            UpdateChecker.ReadLatestReleaseTagAsync(content, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateChecker_FetchLatestReleaseTagAsync_CancelsStalledBody()
    {
        using var client = new HttpClient(new StaticResponseHandler(new StalledContent()))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateChecker.FetchLatestReleaseTagAsync(
                client,
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("~/cdidx-logs", "cdidx-logs")]
    [InlineData("$HOME/cdidx-logs", "cdidx-logs")]
    [InlineData("${HOME}/cdidx-logs", "cdidx-logs")]
    public void GlobalToolLog_OverrideDirectory_ExpandsHomeShorthand(string overrideValue, string childDirectory)
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            "XDG_STATE_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR");
        env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", overrideValue);
        env.Set("XDG_STATE_HOME", null);
        env.Set("XDG_CACHE_HOME", null);
        env.Set("XDG_RUNTIME_DIR", null);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var resolved = GlobalToolLog.ResolveLogDirectoryForReport();

        Assert.Equal(Path.GetFullPath(Path.Combine(home, childDirectory)), resolved);
    }

    [Theory]
    [InlineData("XDG_STATE_HOME", "state-home")]
    [InlineData("XDG_CACHE_HOME", "cache-home")]
    [InlineData("XDG_RUNTIME_DIR", "runtime-dir")]
    public void GlobalToolLog_XdgDirectory_UsesFirstConfiguredTier(string variableName, string directoryName)
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            "XDG_STATE_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR");
        env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", null);
        env.Set("XDG_STATE_HOME", null);
        env.Set("XDG_CACHE_HOME", null);
        env.Set("XDG_RUNTIME_DIR", null);
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_xdg_{Guid.NewGuid():N}");
        var selected = Path.Combine(root, directoryName);
        try
        {
            env.Set(variableName, selected);

            var resolved = GlobalToolLog.ResolveLogDirectoryForReport();

            Assert.Equal(Path.Combine(selected, "cdidx", "logs"), resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void GlobalToolLog_XdgDirectory_HonorsDocumentedPrecedence()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            "XDG_STATE_HOME",
            "XDG_CACHE_HOME",
            "XDG_RUNTIME_DIR");
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_xdg_precedence_{Guid.NewGuid():N}");
        try
        {
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", null);
            env.Set("XDG_STATE_HOME", Path.Combine(root, "state"));
            env.Set("XDG_CACHE_HOME", Path.Combine(root, "cache"));
            env.Set("XDG_RUNTIME_DIR", Path.Combine(root, "runtime"));

            var resolved = GlobalToolLog.ResolveLogDirectoryForReport();

            Assert.Equal(Path.Combine(root, "state", "cdidx", "logs"), resolved);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void GlobalToolLog_TryStart_DisposesWriterWhenStartupAfterWriterCreationFails()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR");
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_fault_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        var writer = new TrackingStreamWriter();

        try
        {
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

            var session = GlobalToolLog.TryStartForTesting(
                ["status"],
                "1.10.0",
                _ => writer,
                () => throw new UnauthorizedAccessException("prune failed"));

            Assert.Null(session);
            Assert.True(writer.WasDisposed);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Theory]
    [InlineData("/repo/src/CodeIndex/bin/Debug/net8.0/")]
    [InlineData("/repo/src/CodeIndex/bin/Debug/net8.0/cdidx.dll")]
    [InlineData("/repo/tests/CodeIndex.Tests/bin/Debug/net8.0/CodeIndex.Tests.dll")]
    [InlineData(@"C:\repo\src\CodeIndex\bin\Debug\net8.0\cdidx.exe")]
    [InlineData(@"C:/repo/src\CodeIndex/bin\Debug/net8.0/cdidx.exe")]
    public void GlobalToolLog_DevelopmentExecutionDetection_RecognizesCanonicalAndMixedSeparators(string path)
    {
        Assert.True(GlobalToolLog.LooksLikeDevelopmentExecutionForTesting(path));
    }

    [Fact]
    public void GlobalToolLog_DevelopmentExecutionDetection_DoesNotMatchPartialDirectoryNames()
    {
        Assert.False(GlobalToolLog.LooksLikeDevelopmentExecutionForTesting("/repo/not-src/CodeIndex/bin/Debug/net8.0/"));
        Assert.False(GlobalToolLog.LooksLikeDevelopmentExecutionForTesting("/repo/src/CodeIndex.Binary/bin/Debug/net8.0/"));
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_WritesLifecycleAndMirrorsStderr()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

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
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_JsonFormatWritesJsonLines()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_json_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            GlobalToolLog.LogFormatEnvironmentVariable,
            GlobalToolLog.LogRetainEnvironmentVariable,
            GlobalToolLog.LogMaxSizeMbEnvironmentVariable);

        try
        {
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

            var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["--log-format", "json", "definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Unknown command: definitely-not-a-command", stderr);

            var logPath = Directory.GetFiles(logDir, "stderr-*.log", SearchOption.TopDirectoryOnly).Single();
            var firstLine = File.ReadLines(logPath).First();
            using var document = JsonDocument.Parse(firstLine);
            Assert.Equal("INFO", document.RootElement.GetProperty("level").GetString());
            Assert.Contains("session_start", document.RootElement.GetProperty("msg").GetString());
            Assert.True(document.RootElement.TryGetProperty("ts", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_SearchQueryThatLooksLikeGlobalLogFlag_IsNotConsumed_Issue2955()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2955_search_log_flag_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "USER_GUIDE.md",
                "markdown",
                "--log-max-size-mb appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "--log-max-size-mb", "--path", "USER_GUIDE.md", "--db", dbPath, "--count", "--exact-substring"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("--log-max-size-mb=50")]
    [InlineData("--log-format=json")]
    public void Run_SearchInlineQueryThatLooksLikeGlobalLogFlag_IsNotConsumed_Issue2955(string query)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2955_search_inline_log_flag_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "README.md",
                "markdown",
                $"{query} appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", query, "--path", "README.md", "--db", dbPath, "--count", "--exact-substring"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_SearchStillConsumesValidGlobalLogFlagBeforeQuery_Issue2955()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2955_search_log_flag_option");
        using var env = EnvironmentVariableScope.Capture(GlobalToolLog.LogMaxSizeMbEnvironmentVariable);
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "USER_GUIDE.md",
                "markdown",
                "needle appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "--log-max-size-mb", "1", "needle", "--path", "USER_GUIDE.md", "--db", dbPath, "--count"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("1", Environment.GetEnvironmentVariable(GlobalToolLog.LogMaxSizeMbEnvironmentVariable));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_SearchStillConsumesInlineGlobalLogFlagAfterQuery_Issue2955()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2955_search_inline_log_flag_after_query");
        using var env = EnvironmentVariableScope.Capture(GlobalToolLog.LogMaxSizeMbEnvironmentVariable);
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "USER_GUIDE.md",
                "markdown",
                "needle appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "needle", "--log-max-size-mb=1", "--path", "USER_GUIDE.md", "--db", dbPath, "--count"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("1", Environment.GetEnvironmentVariable(GlobalToolLog.LogMaxSizeMbEnvironmentVariable));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("--color", "never")]
    [InlineData("--palette", "basic")]
    [InlineData("--trace", "none")]
    public void Run_SearchSeparatedGlobalValueFlagBeforeLogFlagQuery_IsNotMistakenForQuery_Issue2955(string optionName, string optionValue)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2955_search_global_value_before_log_flag_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "USER_GUIDE.md",
                "markdown",
                "--log-max-size-mb appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", optionName, optionValue, "--log-max-size-mb", "--path", "USER_GUIDE.md", "--db", dbPath, "--count", "--exact-substring"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            ConsoleUi.SetColorMode(ColorMode.Auto);
            ConsoleUi.SetColorPalette(null);
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_SearchSeparatedMetricsFlagBeforeLogFlagQuery_IsNotMistakenForQuery_Issue2955()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2955_search_metrics_before_log_flag_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var metricsPath = Path.Combine(projectRoot, "metrics.jsonl");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "USER_GUIDE.md",
                "markdown",
                "--log-max-size-mb appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "--metrics", metricsPath, "--log-max-size-mb", "--path", "USER_GUIDE.md", "--db", dbPath, "--count", "--exact-substring"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("1", stdout.Trim());
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_OnUnix_HardensExistingAndCurrentLogFiles()
    {
        if (OperatingSystem.IsWindows())
            return;

        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_permissions_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            var oldLogPath = Path.Combine(logDir, "stderr-20240101.log");
            File.WriteAllText(oldLogPath, "old log");
            File.SetUnixFileMode(oldLogPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead);

            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

            var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Unknown command: definitely-not-a-command", stderr);

            var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(expectedMode, File.GetUnixFileMode(oldLogPath));

            var currentLogPath = Directory.GetFiles(logDir, "stderr-*.log", SearchOption.TopDirectoryOnly)
                .Single(path => Regex.IsMatch(Path.GetFileName(path), $@"^stderr-{DateTime.UtcNow:yyyyMMdd}-p\d+-\d{{6}}\.log$"));
            Assert.Equal(expectedMode, File.GetUnixFileMode(currentLogPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_WritesUnhandledExceptionChain()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_exception_chain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

            var inner = new InvalidOperationException("root cause");
            var outer = new ApplicationException("outer wrapper", inner);
            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["status"],
                appVersion: "1.10.0",
                beforeDispatchForTesting: () => throw outer));

            Assert.Equal(CommandExitCodes.UnhandledException, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.StartsWith("Error: command failed before it could complete.", stderr.TrimEnd());
            Assert.DoesNotContain("root cause", stderr);

            var logPath = Directory.GetFiles(logDir, "stderr-*.log", SearchOption.TopDirectoryOnly).Single();
            var log = File.ReadAllText(logPath);
            Assert.Contains("unhandled_exception", log);
            Assert.Contains("exception[0] type=System.ApplicationException message=\"outer wrapper\"", log);
            Assert.Contains("inner_exception[1] type=System.InvalidOperationException message=\"root cause\"", log);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_PrunesToThirtyDailyFiles()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_prune_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            for (var i = 0; i < 35; i++)
            {
                var date = new DateTime(2024, 1, 1).AddDays(i);
                File.WriteAllText(Path.Combine(logDir, $"stderr-{date:yyyyMMdd}.log"), $"old {i}");
            }

            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

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
            Assert.Contains(logs, name => Regex.IsMatch(name ?? string.Empty, $@"^stderr-{DateTime.UtcNow:yyyyMMdd}-p\d+-\d{{6}}\.log$"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_HonorsRetainCountAndSizeRotation()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_rotation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            GlobalToolLog.LogFormatEnvironmentVariable,
            GlobalToolLog.LogRetainEnvironmentVariable,
            GlobalToolLog.LogMaxSizeMbEnvironmentVariable,
            GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable);

        try
        {
            var fixedNow = new DateTimeOffset(2026, 5, 31, 12, 34, 56, TimeSpan.Zero);
            GlobalToolLog.TimeProvider = new ManualTimeProvider(fixedNow);
            for (var i = 0; i < 4; i++)
            {
                var path = Path.Combine(logDir, $"stderr-2024010{i + 1}.log");
                File.WriteAllText(path, $"old {i}");
                File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, i + 1, 0, 0, 0, DateTimeKind.Utc));
            }

            var currentPath = Path.Combine(logDir, $"stderr-{fixedNow:yyyyMMdd}-p{Environment.ProcessId}-{fixedNow:HHmmss}.log");
            File.WriteAllBytes(currentPath, new byte[1024 * 1024]);
            File.SetLastWriteTimeUtc(currentPath, DateTime.UtcNow);

            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

            var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["--log-retain-count=2", "--log-max-size-mb=1", "definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Unknown command: definitely-not-a-command", stderr);

            var logs = Directory.GetFiles(logDir, "stderr-*.log", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(2, logs.Length);
            Assert.Contains(logs, name => Regex.IsMatch(name ?? string.Empty, $@"^stderr-{fixedNow:yyyyMMdd}-p\d+-{fixedNow:HHmmss}-1\.log$"));
        }
        finally
        {
            GlobalToolLog.TimeProvider = TimeProvider.System;
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_LogMaxSizeMbAboveMaximum_ReturnsInvalidArgument()
    {
        using var env = EnvironmentVariableScope.Capture(GlobalToolLog.LogMaxSizeMbEnvironmentVariable);
        var tooLarge = GlobalToolLog.MaxLogSizeMb + 1;

        var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
            [$"--log-max-size-mb={tooLarge.ToString(CultureInfo.InvariantCulture)}", "status"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains($"--log-max-size-mb must be an integer between 1 and {GlobalToolLog.MaxLogSizeMb}", stderr);
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_RotatesByDefaultMaxBytesEnvironmentVariable()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_max_bytes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR",
            GlobalToolLog.LogMaxSizeMbEnvironmentVariable,
            GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable);

        try
        {
            var fixedNow = new DateTimeOffset(2026, 5, 31, 12, 35, 56, TimeSpan.Zero);
            GlobalToolLog.TimeProvider = new ManualTimeProvider(fixedNow);
            var currentPrefix = $"stderr-{fixedNow:yyyyMMdd}-p{Environment.ProcessId}-{fixedNow:HHmmss}";
            File.WriteAllBytes(Path.Combine(logDir, $"{currentPrefix}.log"), new byte[64]);

            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", null);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
            env.Set(GlobalToolLog.GlobalToolLogMaxBytesEnvironmentVariable, "64");

            var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Unknown command: definitely-not-a-command", stderr);

            var logs = Directory.GetFiles(logDir, "stderr-*.log", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Contains(logs, name => Regex.IsMatch(name ?? string.Empty, $@"^stderr-{fixedNow:yyyyMMdd}-p\d+-{fixedNow:HHmmss}-1\.log$"));
        }
        finally
        {
            GlobalToolLog.TimeProvider = TimeProvider.System;
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Fact]
    public void Run_ForcedGlobalToolLogging_CanBeDisabledExplicitly()
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_disabled_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", "1");
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

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
            TestProjectHelper.DeleteDirectory(logDir);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("1")]
    public void Run_ForcedGlobalToolLogging_DisableEnvAcceptsTruthyValues(string disabledValue)
    {
        var logDir = Path.Combine(Path.GetTempPath(), $"cdidx_global_tool_log_disabled_bool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDir);
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_FORCE_GLOBAL_TOOL_LOG",
            "CDIDX_DISABLE_PERSISTENT_LOG",
            "CDIDX_GLOBAL_TOOL_LOG_DIR");

        try
        {
            env.Set("CDIDX_FORCE_GLOBAL_TOOL_LOG", "1");
            env.Set("CDIDX_DISABLE_PERSISTENT_LOG", disabledValue);
            env.Set("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);

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
    [InlineData(new[] { "search", "foo", "--color=auto" }, ColorMode.Auto, new[] { "search", "foo" })]
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
    public void TryConsumeColorFlag_QueryCommandFirstLiteral_PreservesFlagLikeQuery()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalMode = ConsoleUi.GetColorMode();
            try
            {
                var args = new[] { "search", "--color", "--path", "src/app.cs" };
                Assert.True(ProgramRunner.TryConsumeColorFlag(ref args, out var error));
                Assert.Empty(error);
                Assert.Equal(ColorMode.Auto, ConsoleUi.GetColorMode());
                Assert.Equal(new[] { "search", "--color", "--path", "src/app.cs" }, args);
            }
            finally
            {
                ConsoleUi.SetColorMode(originalMode);
            }
        }
    }

    [Theory]
    [InlineData("--color")]
    [InlineData("--palette")]
    [InlineData("--metrics")]
    public void RunSearch_FirstQueryLiteralMatchingNonLogGlobalFlag_Issue2975(string query)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2975_global_flag_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                $$"""
                public static class App
                {
                    public const string Flag = "{{query}}";
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", query, "--db", dbPath, "--path", "src/app.cs", "--json", "--exact-substring"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("src/app.cs", stdout);
            Assert.Contains(query, stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_ExistingQueryEscapePreservesFlagLikeQuery_Issue2975()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue2975_existing_query_escape");
        const string query = "--color=auto";
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/app.cs",
                "csharp",
                $$"""
                public static class App
                {
                    public const string Flag = "{{query}}";
                }
                """);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "--", query, "--db", dbPath, "--path", "src/app.cs", "--json", "--exact-substring"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("src/app.cs", stdout);
            Assert.Contains(query, stdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
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
                var args = new[] { "search", "foo", "--color=sparkly" };
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
                var args = new[] { "search", "foo", "--color" };
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
                var args = new[] { "--color=always", "search", "--", "--color=auto" };
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

    [Fact]
    public void CliRecoverableErrors_UseCanonicalHumanFormat_Issue1955()
    {
        var missingProject = Path.Combine(Path.GetTempPath(), $"cdidx_missing_{Guid.NewGuid():N}");

        var cases = new[]
        {
            CaptureConsole(() => ProgramRunner.Run(["--completions"], appVersion: "1.10.0")),
            CaptureConsole(() => IndexCommandRunner.Run([missingProject], new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            CaptureConsole(() => QueryCommandRunner.RunSearch(["Symbol", "--since", "not-a-date"], new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            CaptureConsole(() => QueryCommandRunner.RunSearch(["Symbol", "--db", Path.Combine(missingProject, "missing.db")], new JsonSerializerOptions(JsonSerializerDefaults.Web))),
        };

        Assert.All(cases, result =>
        {
            Assert.NotEqual(CommandExitCodes.Success, result.ExitCode);
            Assert.Equal(string.Empty, result.Stdout);
            AssertCanonicalCommandError(result.Stderr);
        });
    }

    [Theory]
    [InlineData(new[] { "--palette=truecolor", "status" }, ColorPalette.Truecolor, new[] { "status" })]
    [InlineData(new[] { "status", "--palette", "256" }, ColorPalette.Color256, new[] { "status" })]
    [InlineData(new[] { "search", "foo", "--palette=basic" }, ColorPalette.Basic, new[] { "search", "foo" })]
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
                var args = new[] { "search", "foo", "--palette=fancy" };
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
                var args = new[] { "search", "foo", "--palette" };
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
        using var env = EnvironmentVariableScope.Capture(UpdateChecker.DisableEnvVar);
        env.Set(UpdateChecker.DisableEnvVar, "1");
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
        using var env = EnvironmentVariableScope.Capture(UpdateChecker.DisableEnvVar);
        env.Set(UpdateChecker.DisableEnvVar, "1");
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
    public void Run_Version_HumanOutput_AppendsCachedNewerReleaseHint()
    {
        var line = ProgramRunner.FormatVersionLine(
            new ConsoleUi.BuildMetadata("1.10.0", "abc1234", "2026-05-23T00:00:00Z", "clean"),
            "A newer release is available: v1.11.0");

        Assert.Equal("cdidx v1.10.0 (commit abc1234, built 2026-05-23T00:00:00Z, clean) [A newer release is available: v1.11.0]", line);
    }

    [Fact]
    public void UpdateChecker_FreshCache_ReturnsNewerReleaseWithoutFetching()
    {
        using var env = EnvironmentVariableScope.Capture(UpdateChecker.DisableEnvVar);
        env.Set(UpdateChecker.DisableEnvVar, null);
        var cacheDir = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}");
        var cachePath = Path.Combine(cacheDir, "update-check.json");
        Directory.CreateDirectory(cacheDir);
        try
        {
            File.WriteAllText(cachePath, """
                {"checked_at":"2026-05-23T00:00:00.0000000Z","latest_tag":"v1.11.0"}
                """);

            var hint = UpdateChecker.GetNewerReleaseHint(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-05-23T01:00:00Z"),
                _ => throw new InvalidOperationException("should not fetch"));

            Assert.Equal("A newer release is available: v1.11.0", hint);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(cacheDir);
        }
    }

    [Fact]
    public void UpdateChecker_Disabled_ReturnsNullAndDoesNotFetch()
    {
        using var env = EnvironmentVariableScope.Capture(UpdateChecker.DisableEnvVar);
        env.Set(UpdateChecker.DisableEnvVar, "1");

        var hint = UpdateChecker.GetNewerReleaseHint(
            "1.10.0",
            Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json"),
            DateTimeOffset.UtcNow,
            _ => throw new InvalidOperationException("should not fetch"));

        Assert.Null(hint);
    }

    [Fact]
    public void UpdateChecker_GetNewerReleaseHint_PropagatesCallerCancellation()
    {
        using var env = EnvironmentVariableScope.Capture(UpdateChecker.DisableEnvVar);
        env.Set(UpdateChecker.DisableEnvVar, null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            UpdateChecker.GetNewerReleaseHint(
                "1.10.0",
                Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json"),
                DateTimeOffset.UtcNow,
                token => throw new OperationCanceledException(token),
                cts.Token));
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
                var args = new[] { "search", "foo", "--debug-unsafe" };
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
        => ConsoleCapture.Capture(action);

    private static void AssertCanonicalCommandError(string stderr)
    {
        var lines = stderr.TrimEnd().Split(Environment.NewLine);
        Assert.InRange(lines.Length, 2, 3);
        Assert.StartsWith("Error", lines[0]);
        Assert.Contains(": ", lines[0]);
        Assert.StartsWith("Hint: ", lines[1]);
        if (lines.Length == 3)
            Assert.StartsWith("Usage: ", lines[2]);
    }

    private sealed class ThrowingResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            throw new InvalidOperationException(JsonOutputFailure.ReflectionDisabledMessage);
    }

    private sealed class TrackingStreamWriter : StreamWriter
    {
        public TrackingStreamWriter()
            : base(new MemoryStream())
        {
        }

        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
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
    public void TryConsumeAuditLogFlags_MaxBytesAboveMax_ReturnsError()
    {
        var args = new[] { "--audit-log", "/tmp/a.jsonl", "--audit-log-max-bytes", (AuditLogSink.MaxMaxBytes + 1).ToString(CultureInfo.InvariantCulture) };
        var ok = ProgramRunner.TryConsumeAuditLogFlags(ref args, out _, out var error);

        Assert.False(ok);
        Assert.Contains(AuditLogSink.MaxMaxBytes.ToString(CultureInfo.InvariantCulture), error);
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

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpContent _content;

        internal StaticResponseHandler(HttpContent content)
        {
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content });
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.CompletedTask;

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new StalledStream());

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
