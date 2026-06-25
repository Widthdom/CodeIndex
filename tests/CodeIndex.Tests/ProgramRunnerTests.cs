using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.Threading;
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

        env.Set(ProgramRunner.McpHttpTokenEnvVar, string.Empty);
        Assert.Equal("generic-secret", ProgramRunner.ResolveMcpHttpBearerTokenFromEnvironment());

        env.Set(McpAuthenticatorFactory.AuthTokenEnvVar, string.Empty);
        Assert.Null(ProgramRunner.ResolveMcpHttpBearerTokenFromEnvironment());
    }

    [Theory]
    [InlineData(" http-secret")]
    [InlineData("http-secret ")]
    [InlineData("http secret")]
    [InlineData("http-secret\n")]
    public void ResolveMcpHttpBearerTokenFromEnvironment_RejectsWhitespaceOrControlToken_Issue3505(string token)
    {
        using var env = EnvironmentVariableScope.Capture(
            ProgramRunner.McpHttpTokenEnvVar,
            McpAuthenticatorFactory.AuthTokenEnvVar);
        env.Set(ProgramRunner.McpHttpTokenEnvVar, token);
        env.Set(McpAuthenticatorFactory.AuthTokenEnvVar, "generic-secret");

        var ex = Assert.Throws<FormatException>(ProgramRunner.ResolveMcpHttpBearerTokenFromEnvironment);
        Assert.Contains(ProgramRunner.McpHttpTokenEnvVar, ex.Message, StringComparison.Ordinal);

        env.Set(ProgramRunner.McpHttpTokenEnvVar, null);
        env.Set(McpAuthenticatorFactory.AuthTokenEnvVar, token);

        ex = Assert.Throws<FormatException>(ProgramRunner.ResolveMcpHttpBearerTokenFromEnvironment);
        Assert.Contains(McpAuthenticatorFactory.AuthTokenEnvVar, ex.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("recipes", "cdidx recipes")]
    [InlineData("audit", "cdidx audit")]
    public void Run_SearchAuditAliasHelp_PrintsCommandUsage_Issue3893(string command, string expectedUsage)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            [command, "--help"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Contains(expectedUsage, stdout);
        Assert.DoesNotContain("Build or update the index for a project", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void RunRecipesAlias_ListsRecipeJsonObject_Issue3893()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["recipes", "--json"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.TryGetProperty("recipes", out _));
        Assert.True(document.RootElement.TryGetProperty("count", out _));
    }

    [Theory]
    [InlineData("refs", "cdidx references")]
    [InlineData("stats", "cdidx status")]
    [InlineData("fold", "cdidx backfill-fold")]
    public void Run_LegacyAliasHelp_PrintsCommandUsage_Issue3916(string command, string expectedUsage)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            [command, "--help"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Contains(expectedUsage, stdout);
        Assert.DoesNotContain("Build or update the index for a project", stdout);
        Assert.Empty(stderr);
    }

    [Theory]
    [InlineData("db", "checkpoint", "cdidx db checkpoint", "creates a filesystem snapshot")]
    [InlineData("hooks", "install", "cdidx hooks install", ".git/hooks/pre-commit")]
    public void Run_NestedCommandHelp_PrintsSpecificUsage_Issue3916(
        string command,
        string subcommand,
        string expectedUsage,
        string expectedNote)
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            [command, subcommand, "--help"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Contains(expectedUsage, stdout);
        Assert.Contains(expectedNote, stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void Run_UnknownCommandHelpJson_ReturnsJsonUsageError_Issue3916()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["no-such-command", "--help", "--json"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Contains("Unknown command: no-such-command", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Build or update the index for a project", stdout);
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
    public void CanWriteDirectory_ProbeCleanupFailureWarnsWithoutFailing_Issue3024()
    {
        lock (TestConsoleLock.Gate)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"cdidx_install_probe_cleanup_{Guid.NewGuid():N}");
            var originalError = Console.Error;
            using var stderr = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                Directory.CreateDirectory(directory);
                ProgramRunner.DeleteInstallDirectoryWriteProbeForTesting = _ => throw new IOException("simulated probe cleanup failure");
                Console.SetError(stderr);

                Assert.True(ProgramRunner.CanWriteDirectory(directory));

                var warning = stderr.ToString();
                Assert.Contains("Warning: failed to delete install directory write probe", warning);
                Assert.Contains("IOException", warning);
                Assert.Single(Directory.GetFiles(directory, ".cdidx-write-test-*", SearchOption.TopDirectoryOnly));
            }
            finally
            {
                ProgramRunner.DeleteInstallDirectoryWriteProbeForTesting = null;
                Console.SetError(originalError);
                TestProjectHelper.DeleteDirectory(directory);
            }
        }
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
                TestProjectHelper.DeleteDirectory(tempDir);
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
    public void Run_TestExtractor_SourceGrowsAfterLengthCheck_ReturnsInvalidArgument_Issue3075()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_test_extractor_growing_source_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);
                var file = Path.Combine(tempDir, "growing.py");
                File.WriteAllText(file, new string('x', (int)ProgramRunner.TestExtractorMaxInputBytes - 1));
                ProgramRunner.TestExtractorFileLengthCheckedForTesting = checkedPath =>
                {
                    if (checkedPath == file)
                        File.AppendAllText(file, "xx");
                };

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
                ProgramRunner.TestExtractorFileLengthCheckedForTesting = null;
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
    public void Run_TestExtractor_ExpectedSymbolsTooDeep_ReturnsInvalidArgument_Issue3470()
    {
        lock (TestConsoleLock.Gate)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_test_extractor_deep_expect_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempDir);
                var file = Path.Combine(tempDir, "app.py");
                var expect = Path.Combine(tempDir, "expected.json");
                File.WriteAllText(file, "def hello():\n    pass\n");
                File.WriteAllText(expect, BuildNestedJsonArray(ProgramRunner.TestExtractorJsonComparisonMaxDepth + 1));

                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["test-extractor", "--language", "python", "--file", file, "--expect-symbols", expect],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
                Assert.Empty(stdout);
                Assert.Contains("test-extractor expected or actual symbols JSON could not be parsed", stderr);
                Assert.Contains($"{ProgramRunner.TestExtractorJsonComparisonMaxDepth} depth limit", stderr);
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
    public void RunDoctor_TruncatesTerminalEnvironmentValues_Issue3109()
    {
        var prefix = new string('c', ConsoleUi.DefaultDiagnosticValueCharLimit);
        const string tail = "TAIL_ISSUE_3109";
        var raw = prefix + tail;
        using var env = EnvironmentVariableScope.Capture("COLUMNS", "NO_COLOR", "TERM", "CDIDX_VISIBLE_LONG_VALUE");
        env.Set("COLUMNS", raw);
        env.Set("NO_COLOR", raw);
        env.Set("TERM", raw);
        env.Set("CDIDX_VISIBLE_LONG_VALUE", raw);

        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["doctor"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("terminal:", stdout);
        Assert.Contains("cdidx_env:", stdout);
        Assert.Contains($"original length {raw.Length} chars", stdout);
        Assert.DoesNotContain(tail, stdout);
    }

    [Fact]
    public void EnvironmentVariableInventory_IncludesSecretAndPolicyClassifications()
    {
        var byName = EnvironmentVariableInventory.Items.ToDictionary(item => item.Name, StringComparer.Ordinal);

        var githubToken = Assert.Contains("CDIDX_GITHUB_TOKEN", byName);
        Assert.Equal(EnvironmentVariableInventory.SensitivitySecret, githubToken.Sensitivity);
        Assert.Equal("security", githubToken.Policy);
        Assert.Equal("no", githubToken.ConfigFileSupported);
        Assert.Contains(githubToken.Locations, location => location.Path.EndsWith("GitHubIssueReporter.cs", StringComparison.Ordinal));

        var maxLineWidth = Assert.Contains(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, byName);
        Assert.Equal(EnvironmentVariableInventory.SensitivityPublic, maxLineWidth.Sensitivity);
        Assert.Equal("display", maxLineWidth.Policy);
        Assert.Equal("yes", maxLineWidth.ConfigFileSupported);
    }

    [Fact]
    public void RunDoctor_EnvironmentInventory_PrintsAuditView()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["doctor", "--env-inventory"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("environment_inventory:", stdout);
        Assert.Contains("CDIDX_GITHUB_TOKEN", stdout);
        Assert.Contains("sensitivity: secret", stdout);
        Assert.Contains("CDIDX_MCP_RATE_LIMIT_RPS", stdout);
        Assert.Contains("policy   : performance", stdout);
    }

    [Fact]
    public void RunDoctor_Json_PrintsStableSchemaAndRedactsSecrets_Issue3925()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_GITHUB_TOKEN", "CDIDX_VISIBLE_PATH");
        const string secret = "ghp_123456789012345678901234567890123456";
        const string visiblePath = "/tmp/cdidx-visible-path-3925";
        env.Set("CDIDX_GITHUB_TOKEN", secret);
        env.Set("CDIDX_VISIBLE_PATH", visiblePath);

        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["doctor", "--json"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.DoesNotContain(secret, stdout);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("1", root.GetProperty("api_version").GetString());
        Assert.Equal("1.10.0", root.GetProperty("version").GetString());
        Assert.True(root.GetProperty("terminal").TryGetProperty("stdout_tty", out _));
        Assert.True(root.GetProperty("paths").TryGetProperty("db", out _));
        Assert.False(root.GetProperty("redaction").GetProperty("paths_redacted").GetBoolean());
        Assert.True(root.GetProperty("redaction").GetProperty("secrets_redacted").GetBoolean());

        var envByName = root.GetProperty("cdidx_env")
            .EnumerateArray()
            .ToDictionary(
                element => element.GetProperty("name").GetString()!,
                element => element,
                StringComparer.Ordinal);
        Assert.Equal("<redacted>", envByName["CDIDX_GITHUB_TOKEN"].GetProperty("value").GetString());
        Assert.True(envByName["CDIDX_GITHUB_TOKEN"].GetProperty("sensitive").GetBoolean());
        Assert.Equal(visiblePath, envByName["CDIDX_VISIBLE_PATH"].GetProperty("value").GetString());
        Assert.False(envByName["CDIDX_VISIBLE_PATH"].GetProperty("sensitive").GetBoolean());

        var inventoryNames = root.GetProperty("environment_inventory")
            .EnumerateArray()
            .Select(element => element.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("CDIDX_GITHUB_TOKEN", inventoryNames);
        Assert.Contains(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, inventoryNames);
    }

    [Fact]
    public void RunDoctor_JsonRedactPaths_RedactsPathBearingDiagnostics_Issue3925()
    {
        using var env = EnvironmentVariableScope.Capture("CDIDX_VISIBLE_PATH");
        const string visiblePath = "/tmp/cdidx-visible-path-3925";
        env.Set("CDIDX_VISIBLE_PATH", visiblePath);

        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["doctor", "--json", "--redact-paths"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.DoesNotContain(visiblePath, stdout);
        Assert.DoesNotContain(Environment.CurrentDirectory, stdout);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.True(root.GetProperty("redaction").GetProperty("paths_redacted").GetBoolean());
        Assert.Contains("[redacted]", root.GetProperty("cwd").GetString(), StringComparison.Ordinal);

        var visibleEnv = root.GetProperty("cdidx_env")
            .EnumerateArray()
            .Single(element => element.GetProperty("name").GetString() == "CDIDX_VISIBLE_PATH");
        Assert.Contains("[redacted]", visibleEnv.GetProperty("value").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunDoctor_JsonUnsupportedMode_ReturnsStructuredJsonError_Issue3925()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["doctor", "--json=array"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Contains("--json=<format>", root.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunDoctor_JsonIncludesDisplayEnvironmentDecisions_Issue3997()
    {
        using var env = EnvironmentVariableScope.Capture(
            "TERM",
            "TERM_PROGRAM",
            "WT_SESSION",
            "WT_PROFILE_ID",
            "CI",
            "CLICOLOR_FORCE",
            "NO_COLOR",
            "CLICOLOR",
            ConsoleUi.DisableProgressEnvironmentVariable,
            ConsoleUi.PrefersReducedMotionEnvironmentVariable,
            QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable,
            "LC_ALL",
            "LC_CTYPE",
            "LANG",
            "CDIDX_GITHUB_TOKEN");
        const string secret = "ghp_123456789012345678901234567890123456";
        env.Set("TERM", "xterm-256color");
        env.Set("TERM_PROGRAM", null);
        env.Set("WT_SESSION", null);
        env.Set("WT_PROFILE_ID", null);
        env.Set("CI", null);
        env.Set("CLICOLOR_FORCE", "1");
        env.Set("NO_COLOR", null);
        env.Set("CLICOLOR", null);
        env.Set(ConsoleUi.DisableProgressEnvironmentVariable, "1");
        env.Set(ConsoleUi.PrefersReducedMotionEnvironmentVariable, null);
        env.Set(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, "96");
        env.Set("LC_ALL", null);
        env.Set("LC_CTYPE", null);
        env.Set("LANG", "ja_JP.UTF-8");
        env.Set("CDIDX_GITHUB_TOKEN", secret);
        ConsoleUi.SetColorMode(ColorMode.Auto);
        ConsoleUi.SetProgressAnimationEnabled(null);
        try
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["doctor", "--json"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            Assert.DoesNotContain(secret, stdout);
            using var document = JsonDocument.Parse(stdout);
            var display = document.RootElement.GetProperty("display");
            var color = display.GetProperty("color");
            Assert.True(color.GetProperty("enabled").GetBoolean());
            Assert.Equal("CLICOLOR_FORCE", color.GetProperty("source").GetString());
            var progress = display.GetProperty("progress");
            Assert.False(progress.GetProperty("enabled").GetBoolean());
            Assert.Equal(ConsoleUi.DisableProgressEnvironmentVariable, progress.GetProperty("source").GetString());
            var terminalHint = display.GetProperty("terminal_hint");
            Assert.True(terminalHint.GetProperty("has_hint").GetBoolean());
            Assert.Equal("xterm-256color", terminalHint.GetProperty("term").GetString());
            var maxLineWidth = display.GetProperty("max_line_width");
            Assert.Equal(96, maxLineWidth.GetProperty("value").GetInt32());
            Assert.Equal("environment", maxLineWidth.GetProperty("source_kind").GetString());
            Assert.Equal(QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable, maxLineWidth.GetProperty("source").GetString());
            Assert.Equal("parsed", maxLineWidth.GetProperty("status").GetString());
            var ambiguousWidth = display.GetProperty("ambiguous_width");
            Assert.True(ambiguousWidth.GetProperty("wide").GetBoolean());
            Assert.Equal("LANG", ambiguousWidth.GetProperty("source").GetString());
            Assert.Equal("ja_JP.UTF-8", ambiguousWidth.GetProperty("locale").GetString());
            Assert.Equal(LineWidthFormatter.DefaultMaxLineWidth, display.GetProperty("truncation").GetProperty("default_max_line_width").GetInt32());
        }
        finally
        {
            ConsoleUi.SetColorMode(ColorMode.Auto);
            ConsoleUi.SetProgressAnimationEnabled(null);
        }
    }

    [Fact]
    public void Run_QueryTraceStderr_BoundsPathArraysAndValues_Issue3123()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("query-trace-bounds");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "public class App { public void Needle() { } }");
            var longPath = new string('p', ProgramRunner.QueryTraceValueMaxChars) + "TAIL_ISSUE_3123";
            var args = new List<string>
            {
                "search",
                "Needle",
                "--db",
                dbPath,
                "--trace=stderr",
                "--count",
            };
            for (var i = 0; i < ProgramRunner.QueryTraceArrayMaxItems + 3; i++)
            {
                args.Add("--path");
                args.Add(i == 0 ? longPath : $"src/{i}.cs");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                args.ToArray(),
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("0", stdout.Trim());
            var traceLine = stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Single(line => line.StartsWith('{'));
            using var document = JsonDocument.Parse(traceLine);
            var parameters = document.RootElement.GetProperty("parameters");
            Assert.Equal(ProgramRunner.QueryTraceArrayMaxItems, parameters.GetProperty("path").GetArrayLength());
            Assert.True(parameters.GetProperty("path_truncated").GetBoolean());
            Assert.Equal(ProgramRunner.QueryTraceArrayMaxItems + 3, parameters.GetProperty("path_original_count").GetInt32());
            Assert.True(parameters.GetProperty("path_value_truncated").GetBoolean());
            Assert.Contains($"original length {longPath.Length} chars", parameters.GetProperty("path")[0].GetString());
            Assert.DoesNotContain("TAIL_ISSUE_3123", traceLine);
            Assert.DoesNotContain(dbPath, traceLine);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Run_QueryDefaultEnvironmentParseError_TruncatesRawValue_Issue3110()
    {
        var prefix = new string('9', ConsoleUi.DefaultDiagnosticValueCharLimit);
        const string tail = "TAIL_ISSUE_3110";
        var raw = prefix + tail;
        using var env = EnvironmentVariableScope.Capture(QueryCommandRunner.DefaultLimitEnvironmentVariable);
        env.Set(QueryCommandRunner.DefaultLimitEnvironmentVariable, raw);

        var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
            ["search", "Needle"],
            appVersion: "1.10.0"));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Empty(stdout);
        Assert.Contains(QueryCommandRunner.DefaultLimitEnvironmentVariable, stderr);
        Assert.Contains($"original length {raw.Length} chars", stderr);
        Assert.DoesNotContain(tail, stderr);
    }

    [Fact]
    public void WorkspaceVersionPinReadWarning_SanitizesPathLikeExceptionMessages_Issue3218()
    {
        var warning = ProgramRunner.BuildWorkspaceVersionPinReadWarningForTesting(
            new IOException("could not read /Users/alice/private/repo/.cdidx-version"));

        Assert.Equal("Warning: could not read .cdidx-version: read failed.", warning);
        Assert.DoesNotContain("/Users/alice", warning);
        Assert.DoesNotContain("private/repo", warning);
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
            TestProjectHelper.DeleteDirectory(logRoot);
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
            TestProjectHelper.DeleteDirectory(logRoot);
        }
    }

    [Fact]
    public async Task RuntimeTestHooks_AreScopedToExecutionContext()
    {
        Func<HttpClient> upgradeFactory = () => new HttpClient();
        var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
        try
        {
            ProgramRunner.UpgradeHttpClientFactory = upgradeFactory;
            ProgramRunner.TestExtractorFileLengthCheckedForTesting = _ => { };
            ProgramRunner.DeleteInstallDirectoryWriteProbeForTesting = _ => { };
            ProgramRunner.DeleteUpgradeInstallerScriptForTesting = _ => { };
            ProgramRunner.DeleteUpgradeInstallerDirectoryForTesting = _ => { };
            DbWriter.BatchRowSkipWarningForTesting = _ => { };
            DbContext.OptimizePragmaExecutedForTesting = _ => { };

            Task<(
                bool UpgradeFactoryVisible,
                bool TestExtractorHookVisible,
                bool DeleteInstallHookVisible,
                bool DeleteUpgradeHookVisible,
                bool DeleteUpgradeDirectoryHookVisible,
                bool WriterHookVisible,
                bool ContextHookVisible)> task;
            using (ExecutionContext.SuppressFlow())
            {
                task = Task.Run(() => (
                    ReferenceEquals(ProgramRunner.UpgradeHttpClientFactory, upgradeFactory),
                    ProgramRunner.TestExtractorFileLengthCheckedForTesting is not null,
                    ProgramRunner.DeleteInstallDirectoryWriteProbeForTesting is not null,
                    ProgramRunner.DeleteUpgradeInstallerScriptForTesting is not null,
                    ProgramRunner.DeleteUpgradeInstallerDirectoryForTesting is not null,
                    DbWriter.BatchRowSkipWarningForTesting is not null,
                    DbContext.OptimizePragmaExecutedForTesting is not null));
            }

            var observed = await task;
            Assert.False(observed.UpgradeFactoryVisible);
            Assert.False(observed.TestExtractorHookVisible);
            Assert.False(observed.DeleteInstallHookVisible);
            Assert.False(observed.DeleteUpgradeHookVisible);
            Assert.False(observed.DeleteUpgradeDirectoryHookVisible);
            Assert.False(observed.WriterHookVisible);
            Assert.False(observed.ContextHookVisible);
        }
        finally
        {
            ProgramRunner.UpgradeHttpClientFactory = previousFactory;
            ProgramRunner.TestExtractorFileLengthCheckedForTesting = null;
            ProgramRunner.DeleteInstallDirectoryWriteProbeForTesting = null;
            ProgramRunner.DeleteUpgradeInstallerScriptForTesting = null;
            ProgramRunner.DeleteUpgradeInstallerDirectoryForTesting = null;
            DbWriter.BatchRowSkipWarningForTesting = null;
            DbContext.OptimizePragmaExecutedForTesting = null;
        }
    }

    [Fact]
    public void TryConsumeGlobalLogFlags_ReturnsOverridesAndDoesNotMutateEnvironment()
    {
        using var env = EnvironmentVariableScope.Capture(
            GlobalToolLog.LogFormatEnvironmentVariable,
            GlobalToolLog.LogRetainEnvironmentVariable,
            GlobalToolLog.LogMaxSizeMbEnvironmentVariable);
        env.Set(GlobalToolLog.LogFormatEnvironmentVariable, null);
        env.Set(GlobalToolLog.LogRetainEnvironmentVariable, null);
        env.Set(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, null);
        string[] args = ["--log-format", "json", "--log-retain-count", "4", "--log-max-size-mb", "12", "status"];

        var ok = ProgramRunner.TryConsumeGlobalLogFlags(ref args, out var overrides, out var error);

        Assert.True(ok);
        Assert.Empty(error);
        Assert.Equal(["status"], args);
        Assert.Equal("json", overrides[GlobalToolLog.LogFormatEnvironmentVariable]);
        Assert.Equal("4", overrides[GlobalToolLog.LogRetainEnvironmentVariable]);
        Assert.Equal("12", overrides[GlobalToolLog.LogMaxSizeMbEnvironmentVariable]);
        Assert.Null(Environment.GetEnvironmentVariable(GlobalToolLog.LogFormatEnvironmentVariable));
        Assert.Null(Environment.GetEnvironmentVariable(GlobalToolLog.LogRetainEnvironmentVariable));
        Assert.Null(Environment.GetEnvironmentVariable(GlobalToolLog.LogMaxSizeMbEnvironmentVariable));
    }

    [Fact]
    public void TryConsumeSuggestionDedupThresholdFlag_ReturnsOverrideAndRemovesFlag()
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.DedupThresholdEnvironmentVariable);
        env.Set(SuggestionStore.DedupThresholdEnvironmentVariable, null);
        string[] args = ["--db", "index.db", "--suggestion-dedup-threshold", "0.7"];

        var ok = ProgramRunner.TryConsumeSuggestionDedupThresholdFlag(ref args, out var thresholdValue, out var error);

        Assert.True(ok);
        Assert.Empty(error);
        Assert.Equal(["--db", "index.db"], args);
        Assert.Equal("0.7", thresholdValue);
        Assert.Null(Environment.GetEnvironmentVariable(SuggestionStore.DedupThresholdEnvironmentVariable));
    }

    [Fact]
    public void TryConsumeSuggestionDedupThresholdFlag_InvalidValue_ReturnsError()
    {
        using var env = EnvironmentVariableScope.Capture(SuggestionStore.DedupThresholdEnvironmentVariable);
        env.Set(SuggestionStore.DedupThresholdEnvironmentVariable, null);
        string[] args = ["--suggestion-dedup-threshold=1.5"];

        var ok = ProgramRunner.TryConsumeSuggestionDedupThresholdFlag(ref args, out var thresholdValue, out var error);

        Assert.False(ok);
        Assert.Contains("--suggestion-dedup-threshold", error);
        Assert.Null(thresholdValue);
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
            TestProjectHelper.DeleteFile(cachePath);
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
            TestProjectHelper.DeleteFile(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_IgnoresOverDepthCache()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        try
        {
            var depth = UpdateChecker.MaxUpdateCheckCacheJsonDepth + 8;
            File.WriteAllText(cachePath, new string('[', depth) + new string(']', depth));

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
            TestProjectHelper.DeleteFile(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_MalformedCacheDiagnosticsAreGated_Issue3708()
    {
        lock (TestConsoleLock.Gate)
        {
            var cachePathWithoutDiagnostics = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
            var cachePathWithDiagnostics = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
            using var env = EnvironmentVariableScope.Capture(UpdateChecker.DiagnosticsEnvVar);
            var diagnostics = new List<string>();
            UpdateChecker.CacheDiagnosticSinkForTesting = diagnostics.Add;
            try
            {
                File.WriteAllText(cachePathWithoutDiagnostics, "{");
                env.Set(UpdateChecker.DiagnosticsEnvVar, null);

                var quietResult = UpdateChecker.Check(
                    "1.10.0",
                    cachePathWithoutDiagnostics,
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    _ => Task.FromResult<string?>("v1.11.0"));

                Assert.True(quietResult.UpdateAvailable);
                Assert.Empty(diagnostics);

                File.WriteAllText(cachePathWithDiagnostics, "{");
                env.Set(UpdateChecker.DiagnosticsEnvVar, "1");

                var diagnosticResult = UpdateChecker.Check(
                    "1.10.0",
                    cachePathWithDiagnostics,
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    _ => Task.FromResult<string?>("v1.11.0"));

                Assert.True(diagnosticResult.UpdateAvailable);
                var diagnostic = Assert.Single(diagnostics, value => value.Contains("code=cache_read_failed", StringComparison.Ordinal));
                Assert.Contains("update_check_cache_diagnostic", diagnostic);
                Assert.Contains("Json", diagnostic, StringComparison.Ordinal);
            }
            finally
            {
                UpdateChecker.CacheDiagnosticSinkForTesting = null;
                TestProjectHelper.DeleteFile(cachePathWithoutDiagnostics);
                TestProjectHelper.DeleteFile(cachePathWithDiagnostics);
            }
        }
    }

    [Fact]
    public void UpdateChecker_Check_UnwritableCachePathReportsWriteDiagnostic_Issue3708()
    {
        lock (TestConsoleLock.Gate)
        {
            var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_dir_{Guid.NewGuid():N}");
            Directory.CreateDirectory(cachePath);
            using var env = EnvironmentVariableScope.Capture(UpdateChecker.DiagnosticsEnvVar);
            env.Set(UpdateChecker.DiagnosticsEnvVar, "1");
            var diagnostics = new List<string>();
            UpdateChecker.CacheDiagnosticSinkForTesting = diagnostics.Add;
            try
            {
                var result = UpdateChecker.Check(
                    "1.10.0",
                    cachePath,
                    DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    _ => Task.FromResult<string?>("v1.11.0"));

                Assert.True(result.UpdateAvailable);
                var diagnostic = Assert.Single(diagnostics);
                Assert.Contains("update_check_cache_diagnostic", diagnostic);
                Assert.Contains("code=cache_write_failed", diagnostic);
            }
            finally
            {
                UpdateChecker.CacheDiagnosticSinkForTesting = null;
                TestProjectHelper.DeleteDirectory(cachePath);
            }
        }
    }

    [Fact]
    public void UpdateChecker_Check_TransientFailureDoesNotRefreshStaleCache_Issue3822()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        var checkedAt = DateTimeOffset.Parse("2025-12-31T00:00:00Z", CultureInfo.InvariantCulture);
        var originalCache =
            $$"""{"checked_at":"{{checkedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}}","latest_tag":"v9.9.9"}""";
        try
        {
            File.WriteAllText(cachePath, originalCache);

            var result = UpdateChecker.Check(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture),
                _ => throw new HttpRequestException("secret host detail"));

            Assert.Equal("network_failure", result.Error);
            Assert.Equal("v9.9.9", result.LatestVersion);
            Assert.True(result.UpdateAvailable);
            Assert.False(result.FromCache);
            Assert.Equal(originalCache, File.ReadAllText(cachePath));
        }
        finally
        {
            TestProjectHelper.DeleteFile(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_NullFetchDoesNotCreateCache_Issue3822()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        try
        {
            var result = UpdateChecker.Check(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture),
                _ => Task.FromResult<string?>(null));

            Assert.Null(result.LatestVersion);
            Assert.False(result.UpdateAvailable);
            Assert.False(File.Exists(cachePath));
        }
        finally
        {
            TestProjectHelper.DeleteFile(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_ResolveDefaultCachePath_IgnoresRelativeXdgCacheHome_Issue3822()
    {
        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture("XDG_CACHE_HOME", UpdateChecker.DiagnosticsEnvVar);
            env.Set("XDG_CACHE_HOME", "relative-cache-root");
            env.Set(UpdateChecker.DiagnosticsEnvVar, "1");
            var diagnostics = new List<string>();
            UpdateChecker.CacheDiagnosticSinkForTesting = diagnostics.Add;
            try
            {
                var path = UpdateChecker.ResolveDefaultCachePath();

                Assert.True(Path.IsPathFullyQualified(path));
                Assert.DoesNotContain("relative-cache-root", path, StringComparison.Ordinal);
                var diagnostic = Assert.Single(diagnostics, value => value.Contains("code=cache_root_invalid", StringComparison.Ordinal));
                Assert.Contains("update_check_cache_diagnostic", diagnostic);
            }
            finally
            {
                UpdateChecker.CacheDiagnosticSinkForTesting = null;
            }
        }
    }

    [Fact]
    public void UpdateChecker_ResolveDefaultCachePath_UsesScopedCdidxEnvironmentOverride_Issue3690()
    {
        var previous = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"cdidx_cache_scope_{Guid.NewGuid():N}");
        using var env = CdidxEnvironment.Push(new Dictionary<string, string>
        {
            ["XDG_CACHE_HOME"] = cacheRoot,
        });

        var path = UpdateChecker.ResolveDefaultCachePath();

        Assert.Equal(previous, Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));
        Assert.Equal(Path.Combine(Path.GetFullPath(cacheRoot), "cdidx", "update-check.json"), path);
    }

    [Fact]
    public void UpdateChecker_Check_RateLimitResponseReportsRetryMetadata_Issue3822()
    {
        lock (TestConsoleLock.Gate)
        {
            var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
            var now = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);
            var expectedRetryAt = now.UtcDateTime.AddSeconds(90);
            var previousTimeProvider = UpdateChecker.TimeProvider;
            UpdateChecker.TimeProvider = new FixedTimeProvider(now);
            try
            {
                using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(
                    """{"message":"rate limited","token":"secret-token"}"""));
                var handler = new StaticResponseHandler(content, (HttpStatusCode)429)
                {
                    ConfigureResponse = response =>
                        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                            TimeSpan.FromSeconds(90)),
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };

                var result = UpdateChecker.Check(
                    "1.10.0",
                    cachePath,
                    now,
                    token => UpdateChecker.FetchLatestReleaseTagAsync(client, TimeSpan.FromSeconds(1), token));

                Assert.Equal("rate_limited", result.Error);
                Assert.Equal("rate_limit", result.ErrorCategory);
                Assert.Contains("429", result.ErrorHint);
                Assert.Contains("next_retry_at=", result.ErrorHint);
                Assert.Contains(expectedRetryAt.ToString("O", CultureInfo.InvariantCulture), result.ErrorHint);
                Assert.DoesNotContain("secret-token", JsonSerializer.Serialize(result), StringComparison.Ordinal);
                Assert.False(File.Exists(cachePath));
            }
            finally
            {
                UpdateChecker.TimeProvider = previousTimeProvider;
                TestProjectHelper.DeleteFile(cachePath);
            }
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
            TestProjectHelper.DeleteFile(cachePath);
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
            TestProjectHelper.DeleteFile(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_ClassifiesTimeoutFailureWithoutRawMessage_Issue3453()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        try
        {
            var result = UpdateChecker.Check(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                _ => throw new OperationCanceledException("secret timeout detail"));

            Assert.Equal("timeout", result.Error);
            Assert.Equal("timeout", result.ErrorCategory);
            Assert.Contains("Retry later", result.ErrorHint);
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteFile(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_ClassifiesNetworkFailureWithoutRawMessage_Issue3453()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        try
        {
            var result = UpdateChecker.Check(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                _ => throw new HttpRequestException("secret host detail"));

            Assert.Equal("network_failure", result.Error);
            Assert.Equal("network", result.ErrorCategory);
            Assert.Contains("GitHub releases", result.ErrorHint);
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteFile(cachePath);
        }
    }

    [Fact]
    public void UpdateChecker_Check_SerializesStructuredFailureFields_Issue3453()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), $"cdidx_update_check_{Guid.NewGuid():N}.json");
        try
        {
            var result = UpdateChecker.Check(
                "1.10.0",
                cachePath,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                _ => throw new JsonException("secret parser detail"));

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
            var root = json.RootElement;
            Assert.Equal("invalid_response", root.GetProperty("error").GetString());
            Assert.Equal("response", root.GetProperty("error_category").GetString());
            Assert.Contains("safe response bounds", root.GetProperty("error_hint").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret", root.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteFile(cachePath);
        }
    }

    [Theory]
    [InlineData("v1.26.0", "https://github.com/Widthdom/CodeIndex/releases/download/v1.26.0/install.sh")]
    [InlineData(" release/test ", "https://github.com/Widthdom/CodeIndex/releases/download/release%2Ftest/install.sh")]
    public void BuildInstallerScriptUrl_UsesResolvedReleaseTag(string releaseTag, string expected)
    {
        Assert.Equal(expected, ProgramRunner.BuildInstallerScriptUrl(releaseTag));
    }

    [Theory]
    [InlineData("v1.26.0", "install.sh", "https://github.com/Widthdom/CodeIndex/releases/download/v1.26.0/install.sh")]
    [InlineData(" release/test ", "sha256sums.txt", "https://github.com/Widthdom/CodeIndex/releases/download/release%2Ftest/sha256sums.txt")]
    public void BuildReleaseAssetUrl_UsesResolvedReleaseTagAndAsset(string releaseTag, string assetName, string expected)
    {
        Assert.Equal(expected, ProgramRunner.BuildReleaseAssetUrl(releaseTag, assetName));
    }

    [Fact]
    public void GetReleaseAssetChecksum_FindsInstallerScriptEntry()
    {
        var expected = new string('a', 64);
        var manifest = $"""
{new string('b', 64)}  CodeIndex-linux-x64.tar.gz
{expected}  install.sh
""";

        var checksum = ProgramRunner.GetReleaseAssetChecksum(manifest, "install.sh");

        Assert.Equal(expected, checksum);
    }

    [Fact]
    public void GetReleaseAssetChecksum_RequiresInstallerScriptEntry()
    {
        var manifest = $"{new string('b', 64)}  CodeIndex-linux-x64.tar.gz\n";

        var ex = Assert.Throws<InvalidDataException>(() =>
            ProgramRunner.GetReleaseAssetChecksum(manifest, "install.sh"));

        Assert.Contains("install.sh", ex.Message);
    }

    [Fact]
    public void VerifyFileSha256_AcceptsExpectedDigest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx-install-checksum-{Guid.NewGuid():N}.sh");
        var content = Encoding.UTF8.GetBytes("#!/bin/sh\necho ok\n");
        File.WriteAllBytes(path, content);
        try
        {
            var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

            ProgramRunner.VerifyFileSha256(path, expected, "install.sh");
        }
        finally
        {
            TestProjectHelper.DeleteFile(path);
        }
    }

    [Fact]
    public void VerifyFileSha256_RejectsMismatchedDigest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx-install-checksum-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, "#!/bin/sh\necho ok\n");
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() =>
                ProgramRunner.VerifyFileSha256(path, new string('0', 64), "install.sh"));

            Assert.Contains("checksum mismatch", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteFile(path);
        }
    }

    [Fact]
    public void CreateInstallerProcessStartInfo_UsesExplicitLaunchContract_Issue3685()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx installer launch {Guid.NewGuid():N}");
        var script = Path.Combine(root, "install script's path.sh");
        var startInfo = ProgramRunner.CreateInstallerProcessStartInfo(
            script,
            "v1.27.0-rc.1",
            "/opt/cdidx install & safe");

        Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
        Assert.Equal("bash", Path.GetFileName(startInfo.FileName));
        Assert.NotEqual("bash", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(Path.GetFullPath(root), startInfo.WorkingDirectory);
        Assert.Equal([Path.GetFullPath(script), "v1.27.0-rc.1"], startInfo.ArgumentList.ToArray());
        Assert.Equal("/opt/cdidx install & safe", startInfo.Environment["CDIDX_INSTALL_DIR"]);
    }

    [Fact]
    public void CreateInstallerProcessStartInfo_ScrubsEnvironmentByAllowlist_Issue3910()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture(
                "HTTPS_PROXY",
                "CDIDX_VERIFY_POLICY",
                "CDIDX_TEST_INSTALLER_POLICY_3910",
                "CDIDX_SECRET_INSTALLER_POLICY_3910");
            env.Set("HTTPS_PROXY", "http://proxy.example.test:8080");
            env.Set("CDIDX_VERIFY_POLICY", "strict");
            env.Set("CDIDX_TEST_INSTALLER_POLICY_3910", "test-only");
            env.Set("CDIDX_SECRET_INSTALLER_POLICY_3910", "secret");
            var root = Path.Combine(Path.GetTempPath(), $"cdidx installer env {Guid.NewGuid():N}");
            var script = Path.Combine(root, "install.sh");

            var startInfo = ProgramRunner.CreateInstallerProcessStartInfo(script, "v1.27.0", root);

            Assert.Equal("http://proxy.example.test:8080", startInfo.Environment["HTTPS_PROXY"]);
            Assert.Equal("strict", startInfo.Environment["CDIDX_VERIFY_POLICY"]);
            Assert.Equal(root, startInfo.Environment["CDIDX_INSTALL_DIR"]);
            Assert.False(startInfo.Environment.ContainsKey("CDIDX_TEST_INSTALLER_POLICY_3910"));
            Assert.False(startInfo.Environment.ContainsKey("CDIDX_SECRET_INSTALLER_POLICY_3910"));
        }
    }

    [Fact]
    public void RunInstallerProcess_StartFailureReturnsInstallError_Issue3685()
    {
        lock (TestConsoleLock.Gate)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cdidx_installer_start_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(root, "missing-installer-shell"),
                    UseShellExecute = false,
                };
                startInfo.ArgumentList.Add(Path.Combine(root, "install script.sh"));

                var (exitCode, stdout, stderr) = CaptureConsole(() =>
                    ProgramRunner.RunInstallerProcess(startInfo, TimeSpan.FromSeconds(10)));

                Assert.Equal(CommandExitCodes.InstallError, exitCode);
                Assert.Empty(stdout);
                Assert.Contains("failed to start install.sh for upgrade", stderr);
                Assert.Contains("rerun `install.sh` manually", stderr);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(root);
            }
        }
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

                Assert.Equal(CommandExitCodes.InstallError, exitCode);
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
    public void RunInstallerProcess_CancelsHungInstaller()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cdidx_installer_cancel_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var script = Path.Combine(root, "install.sh");
            var pidFile = Path.Combine(root, "installer.pid");
            try
            {
                File.WriteAllText(script, $"""
#!/bin/sh
echo $$ > {ShellQuote(pidFile)}
sleep 30
""");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var startInfo = ProgramRunner.CreateInstallerProcessStartInfo(script, "v1.27.0", root);
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

                Assert.ThrowsAny<OperationCanceledException>(() =>
                    ProgramRunner.RunInstallerProcess(startInfo, TimeSpan.FromSeconds(30), cts.Token));

                Assert.True(File.Exists(pidFile));
                var pid = int.Parse(File.ReadAllText(pidFile), CultureInfo.InvariantCulture);
                Assert.False(IsProcessRunning(pid));
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(root);
            }
        }
    }

    [Fact]
    public void RunInstallerProcess_SuppressedOutputDrainsLargeStreams_Issue3376()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cdidx_installer_output_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var script = Path.Combine(root, "install.sh");
            try
            {
                File.WriteAllText(script, """
#!/bin/sh
i=0
while [ "$i" -lt 2000 ]; do
  printf 'stdout-line-%04d-abcdefghijklmnopqrstuvwxyz\n' "$i"
  printf 'stderr-line-%04d-abcdefghijklmnopqrstuvwxyz\n' "$i" >&2
  i=$((i + 1))
done
exit 0
""");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var startInfo = ProgramRunner.CreateInstallerProcessStartInfo(script, "v1.27.0", root);

                var (exitCode, stdout, stderr) = CaptureConsole(() =>
                    ProgramRunner.RunInstallerProcess(
                        startInfo,
                        TimeSpan.FromSeconds(10),
                        suppressOutput: true));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stdout);
                Assert.Empty(stderr);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(root);
            }
        }
    }

    [Fact]
    public void RunInstallerProcessDetailed_SuppressedFailureCapturesBoundedTail_Issue3831()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            var root = Path.Combine(Path.GetTempPath(), $"cdidx_installer_tail_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var script = Path.Combine(root, "install.sh");
            try
            {
                File.WriteAllText(script, """
#!/bin/sh
i=0
while [ "$i" -lt 700 ]; do
  printf 'stdout-tail-%04d-abcdefghijklmnopqrstuvwxyz\n' "$i"
  printf 'stderr-tail-%04d-abcdefghijklmnopqrstuvwxyz\n' "$i" >&2
  i=$((i + 1))
done
exit 7
""");
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var startInfo = ProgramRunner.CreateInstallerProcessStartInfo(script, "v1.27.0", root);

                var result = ProgramRunner.RunInstallerProcessDetailed(
                    startInfo,
                    TimeSpan.FromSeconds(10),
                    suppressOutput: true);

                Assert.Equal(7, result.ExitCode);
                Assert.True(result.OutputTruncated);
                Assert.True(result.StdoutTail!.Length <= ProgramRunner.InstallerSuppressedOutputTailChars);
                Assert.True(result.StderrTail!.Length <= ProgramRunner.InstallerSuppressedOutputTailChars);
                Assert.Contains("stdout-tail-0699", result.StdoutTail);
                Assert.Contains("stderr-tail-0699", result.StderrTail);
                Assert.DoesNotContain("stdout-tail-0000", result.StdoutTail);
                Assert.DoesNotContain("stderr-tail-0000", result.StderrTail);
            }
            finally
            {
                TestProjectHelper.DeleteDirectory(root);
            }
        }
    }

    [Fact]
    public void RunUpgrade_JsonPreparationFailure_UsesInstallError_Issue3373()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture("XDG_CACHE_HOME", UpdateChecker.DisableEnvVar);
            var cacheRoot = Path.Combine(Path.GetTempPath(), $"cdidx_update_cache_{Guid.NewGuid():N}");
            env.Set("XDG_CACHE_HOME", cacheRoot);
            env.Set(UpdateChecker.DisableEnvVar, null);
            WriteFreshUpdateCheckCache(cacheRoot, "v9.9.9");

            var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
            ProgramRunner.UpgradeHttpClientFactory = () => new HttpClient(
                new StaticResponseHandler(new ByteArrayContent(Encoding.UTF8.GetBytes("missing installer checksum\n"))))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            try
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["upgrade", "--json"],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.InstallError, exitCode);
                Assert.Empty(stderr);
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                Assert.False(root.GetProperty("install_attempted").GetBoolean());
                Assert.Equal("InvalidDataException", root.GetProperty("error").GetString());
            }
            finally
            {
                ProgramRunner.UpgradeHttpClientFactory = previousFactory;
                TestProjectHelper.DeleteDirectory(cacheRoot);
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
            TestProjectHelper.DeleteFile(path);
        }
    }

    [Fact]
    public void RunUpgrade_PassesCallerCancellationToReleaseDownloads()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture("XDG_CACHE_HOME", UpdateChecker.DisableEnvVar);
            var cacheRoot = Path.Combine(Path.GetTempPath(), $"cdidx_update_cache_{Guid.NewGuid():N}");
            env.Set("XDG_CACHE_HOME", cacheRoot);
            env.Set(UpdateChecker.DisableEnvVar, null);
            WriteFreshUpdateCheckCache(cacheRoot, "v9.9.9");

            var installerScript = "#!/bin/sh\nexit 0\n";
            var installerSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installerScript))).ToLowerInvariant();
            var checksumManifest = $"{installerSha256}  install.sh\n";
            var observedCanBeCanceled = new List<bool>();
            var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
            ProgramRunner.UpgradeHttpClientFactory = () => new HttpClient(
                new UpgradeAssetResponseHandler(
                    checksumManifest,
                    installerScript,
                    token => observedCanBeCanceled.Add(token.CanBeCanceled)))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            using var cts = new CancellationTokenSource();
            try
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["upgrade"],
                    appVersion: "1.10.0",
                    cancellationToken: cts.Token));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stdout);
                Assert.Equal(
                    $"Verifying install.sh checksum...{Environment.NewLine}Verified install.sh checksum.{Environment.NewLine}",
                    stderr.ToString());
                Assert.Equal([true, true], observedCanBeCanceled);
            }
            finally
            {
                ProgramRunner.UpgradeHttpClientFactory = previousFactory;
                TestProjectHelper.DeleteDirectory(cacheRoot);
            }
        }
    }

    [Fact]
    public void RunUpgrade_JsonUpdateAvailable_EmitsSingleJsonInstallResult()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture("XDG_CACHE_HOME", UpdateChecker.DisableEnvVar);
            var cacheRoot = Path.Combine(Path.GetTempPath(), $"cdidx_update_cache_{Guid.NewGuid():N}");
            env.Set("XDG_CACHE_HOME", cacheRoot);
            env.Set(UpdateChecker.DisableEnvVar, null);
            WriteFreshUpdateCheckCache(cacheRoot, "v9.9.9");

            var installerScript = """
#!/bin/sh
echo SHOULD_NOT_LEAK_STDOUT
echo SHOULD_NOT_LEAK_STDERR >&2
exit 0
""";
            var installerSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installerScript))).ToLowerInvariant();
            var checksumManifest = $"{installerSha256}  install.sh\n";
            var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
            ProgramRunner.UpgradeHttpClientFactory = () => new HttpClient(
                new UpgradeAssetResponseHandler(
                    checksumManifest,
                    installerScript,
                    _ => { }))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            try
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["upgrade", "--json"],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stderr);
                Assert.DoesNotContain("SHOULD_NOT_LEAK", stdout);
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                Assert.Equal("1.10.0", root.GetProperty("current_version").GetString());
                Assert.Equal("v9.9.9", root.GetProperty("latest_version").GetString());
                Assert.True(root.GetProperty("update_available").GetBoolean());
                Assert.True(root.GetProperty("from_cache").GetBoolean());
                Assert.True(root.GetProperty("install_attempted").GetBoolean());
                Assert.Equal(CommandExitCodes.Success, root.GetProperty("install_exit_code").GetInt32());
                Assert.True(root.GetProperty("install_succeeded").GetBoolean());
            }
            finally
            {
                ProgramRunner.UpgradeHttpClientFactory = previousFactory;
                TestProjectHelper.DeleteDirectory(cacheRoot);
            }
        }
    }

    [Fact]
    public void RunUpgrade_JsonInstallerFailureIncludesSuppressedOutputTail_Issue3831()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture("XDG_CACHE_HOME", UpdateChecker.DisableEnvVar);
            var cacheRoot = Path.Combine(Path.GetTempPath(), $"cdidx_update_cache_{Guid.NewGuid():N}");
            env.Set("XDG_CACHE_HOME", cacheRoot);
            env.Set(UpdateChecker.DisableEnvVar, null);
            WriteFreshUpdateCheckCache(cacheRoot, "v9.9.9");

            var installerScript = """
#!/bin/sh
i=0
while [ "$i" -lt 700 ]; do
  printf 'json-stdout-%04d-abcdefghijklmnopqrstuvwxyz\n' "$i"
  printf 'json-stderr-%04d-abcdefghijklmnopqrstuvwxyz\n' "$i" >&2
  i=$((i + 1))
done
exit 7
""";
            var installerSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installerScript))).ToLowerInvariant();
            var checksumManifest = $"{installerSha256}  install.sh\n";
            var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
            ProgramRunner.UpgradeHttpClientFactory = () => new HttpClient(
                new UpgradeAssetResponseHandler(
                    checksumManifest,
                    installerScript,
                    _ => { }))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            try
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["upgrade", "--json"],
                    appVersion: "1.10.0"));

                Assert.Equal(7, exitCode);
                Assert.Empty(stderr);
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                Assert.True(root.GetProperty("install_attempted").GetBoolean());
                Assert.False(root.GetProperty("install_succeeded").GetBoolean());
                Assert.Equal(7, root.GetProperty("install_exit_code").GetInt32());
                Assert.Equal("installer_exit_code_7", root.GetProperty("error").GetString());
                Assert.True(root.GetProperty("installer_output_truncated").GetBoolean());
                Assert.Contains("json-stdout-0699", root.GetProperty("installer_stdout_tail").GetString(), StringComparison.Ordinal);
                Assert.Contains("json-stderr-0699", root.GetProperty("installer_stderr_tail").GetString(), StringComparison.Ordinal);
                Assert.DoesNotContain("json-stdout-0000", root.GetProperty("installer_stdout_tail").GetString(), StringComparison.Ordinal);
            }
            finally
            {
                ProgramRunner.UpgradeHttpClientFactory = previousFactory;
                TestProjectHelper.DeleteDirectory(cacheRoot);
            }
        }
    }

    [Fact]
    public void RunUpgrade_InstallerScriptCleanupFailure_EmitsWarning_Issue3372()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture("XDG_CACHE_HOME", UpdateChecker.DisableEnvVar);
            var cacheRoot = Path.Combine(Path.GetTempPath(), $"cdidx_update_cache_{Guid.NewGuid():N}");
            env.Set("XDG_CACHE_HOME", cacheRoot);
            env.Set(UpdateChecker.DisableEnvVar, null);
            WriteFreshUpdateCheckCache(cacheRoot, "v9.9.9");

            var installerScript = "#!/bin/sh\nexit 0\n";
            var installerSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installerScript))).ToLowerInvariant();
            var checksumManifest = $"{installerSha256}  install.sh\n";
            var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
            var previousDelete = ProgramRunner.DeleteUpgradeInstallerScriptForTesting;
            ProgramRunner.UpgradeHttpClientFactory = () => new HttpClient(
                new UpgradeAssetResponseHandler(
                    checksumManifest,
                    installerScript,
                    _ => { }))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            ProgramRunner.DeleteUpgradeInstallerScriptForTesting = _ => throw new IOException("delete denied");

            try
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["upgrade", "--json"],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                using var doc = JsonDocument.Parse(stdout);
                Assert.True(doc.RootElement.GetProperty("install_succeeded").GetBoolean());
                Assert.Contains("Warning: failed to delete upgrade installer script", stderr);
                Assert.Contains("IOException", stderr);
            }
            finally
            {
                ProgramRunner.UpgradeHttpClientFactory = previousFactory;
                ProgramRunner.DeleteUpgradeInstallerScriptForTesting = previousDelete;
                TestProjectHelper.DeleteDirectory(cacheRoot);
            }
        }
    }

    [Fact]
    public void RunUpgrade_InstallerDirectoryCleanupFailure_EmitsWarning_Issue3732()
    {
        if (OperatingSystem.IsWindows())
            return;

        lock (TestConsoleLock.Gate)
        {
            using var env = EnvironmentVariableScope.Capture("XDG_CACHE_HOME", UpdateChecker.DisableEnvVar);
            var cacheRoot = Path.Combine(Path.GetTempPath(), $"cdidx_update_cache_{Guid.NewGuid():N}");
            env.Set("XDG_CACHE_HOME", cacheRoot);
            env.Set(UpdateChecker.DisableEnvVar, null);
            WriteFreshUpdateCheckCache(cacheRoot, "v9.9.9");

            var installerScript = "#!/bin/sh\nexit 0\n";
            var installerSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installerScript))).ToLowerInvariant();
            var checksumManifest = $"{installerSha256}  install.sh\n";
            var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
            var previousDelete = ProgramRunner.DeleteUpgradeInstallerDirectoryForTesting;
            string? cleanupDirectory = null;
            ProgramRunner.UpgradeHttpClientFactory = () => new HttpClient(
                new UpgradeAssetResponseHandler(
                    checksumManifest,
                    installerScript,
                    _ => { }))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            ProgramRunner.DeleteUpgradeInstallerDirectoryForTesting = path =>
            {
                cleanupDirectory = path;
                throw new IOException("directory delete denied");
            };

            try
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["upgrade", "--json"],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                using var doc = JsonDocument.Parse(stdout);
                Assert.True(doc.RootElement.GetProperty("install_succeeded").GetBoolean());
                Assert.Contains("Warning: failed to delete upgrade installer temporary directory", stderr);
                Assert.Contains("IOException", stderr);
                Assert.NotNull(cleanupDirectory);
                Assert.True(Directory.Exists(cleanupDirectory));
            }
            finally
            {
                ProgramRunner.UpgradeHttpClientFactory = previousFactory;
                ProgramRunner.DeleteUpgradeInstallerDirectoryForTesting = previousDelete;
                if (cleanupDirectory != null)
                    TestProjectHelper.DeleteDirectory(cleanupDirectory);
                TestProjectHelper.DeleteDirectory(cacheRoot);
            }
        }
    }

    [Fact]
    public void UpdateChecker_Check_WritesCacheWithPrivateModes_Issue3411()
    {
        using var env = EnvironmentVariableScope.Capture(UpdateChecker.DisableEnvVar);
        env.Set(UpdateChecker.DisableEnvVar, null);
        var cacheRoot = Path.Combine(Path.GetTempPath(), $"cdidx_update_cache_private_{Guid.NewGuid():N}");
        var cachePath = Path.Combine(cacheRoot, "cdidx", "update-check.json");
        try
        {
            var result = UpdateChecker.Check(
                "1.0.0",
                cachePath,
                DateTimeOffset.UtcNow,
                _ => Task.FromResult<string?>("v9.9.9"));

            Assert.True(result.UpdateAvailable);
            Assert.True(File.Exists(cachePath));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    DataDirectorySecurity.PrivateDirectoryMode,
                    File.GetUnixFileMode(Path.GetDirectoryName(cachePath)!) & DataDirectorySecurity.PermissionBits);
                Assert.Equal(
                    DataDirectorySecurity.PrivateFileMode,
                    File.GetUnixFileMode(cachePath) & DataDirectorySecurity.PermissionBits);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(cacheRoot);
        }
    }

    [Fact]
    public void RunUpgrade_CheckOnlyJsonExplicitVersion_ReportsSelection()
    {
        lock (TestConsoleLock.Gate)
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["upgrade", "--check-only", "--json", "--version", "2.0.0-rc.1"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            Assert.Equal("v2.0.0-rc.1", root.GetProperty("latest_version").GetString());
            Assert.Equal("v2.0.0-rc.1", root.GetProperty("selected_version").GetString());
            Assert.Equal("prerelease", root.GetProperty("selected_channel").GetString());
            Assert.Equal("explicit_version", root.GetProperty("selection_source").GetString());
            Assert.True(root.GetProperty("include_prerelease").GetBoolean());
            Assert.False(root.GetProperty("install_attempted").GetBoolean());
            Assert.Equal("same_release_sha256_manifest", root.GetProperty("installer_verification").GetString());
            Assert.Contains("same GitHub release asset namespace", root.GetProperty("installer_trust_boundary").GetString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("1.2.3", false)]
    [InlineData("v1.2.3-rc.1", true)]
    [InlineData("v1.2", false)]
    [InlineData("v1.2.3/evil", false)]
    [InlineData("v1.2.3+build", false)]
    public void IsValidUpgradeReleaseTag_ConstrainShape_Issue3831(string releaseTag, bool expected)
    {
        Assert.Equal(expected, ProgramRunner.IsValidUpgradeReleaseTag(releaseTag));
    }

    [Fact]
    public void RunUpgrade_InvalidExplicitVersion_ReturnsUsageError_Issue3831()
    {
        lock (TestConsoleLock.Gate)
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["upgrade", "--check-only", "--version", "release/test"],
                appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Empty(stdout);
            Assert.Contains("vX.Y.Z", stderr);
        }
    }

    [Fact]
    public void TryCheckInstallDirectoryWritable_FilePathReportsDiagnostic_Issue3831()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_install_dir_file_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "");

            var writable = ProgramRunner.TryCheckInstallDirectoryWritable(path, out var diagnostic);

            Assert.False(writable);
            Assert.Equal("IOException", diagnostic);
        }
        finally
        {
            TestProjectHelper.DeleteFile(path);
        }
    }

    [Fact]
    public void TryCheckInstallDirectoryWritable_RootPathRejected_Issue3733()
    {
        var rootPath = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.False(string.IsNullOrEmpty(rootPath));

        var writable = ProgramRunner.TryCheckInstallDirectoryWritable(rootPath!, out var diagnostic);

        Assert.False(writable);
        Assert.Contains("filesystem root", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCheckInstallDirectoryWritable_SymlinkRejected_Issue3733()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_install_dir_link_{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);

            var writable = ProgramRunner.TryCheckInstallDirectoryWritable(link, out var diagnostic);

            Assert.False(writable);
            Assert.Contains("symbolic link", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void TryCheckInstallDirectoryWritable_GroupWritableDirectoryRejected_Issue3733()
    {
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"cdidx_install_dir_mode_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(
                path,
                DataDirectorySecurity.PrivateDirectoryMode | UnixFileMode.GroupWrite);

            var writable = ProgramRunner.TryCheckInstallDirectoryWritable(path, out var diagnostic);

            Assert.False(writable);
            Assert.Contains("group- or world-writable", diagnostic, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(path))
                File.SetUnixFileMode(path, DataDirectorySecurity.PrivateDirectoryMode);
            TestProjectHelper.DeleteDirectory(path);
        }
    }

    [Fact]
    public void TryValidateUpgradeInstallerDirectoryCleanupTarget_RejectsOutsideTempRoot_Issue3659()
    {
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var outside = Path.GetPathRoot(tempRoot);
        Assert.False(string.IsNullOrEmpty(outside));
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(tempRoot),
                Path.TrimEndingDirectorySeparator(outside!),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return;
        }

        var valid = ProgramRunner.TryValidateUpgradeInstallerDirectoryCleanupTarget(
            outside!,
            out _,
            out var failureReason);

        Assert.False(valid);
        Assert.Contains("outside the expected cleanup root", failureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateUpgradeInstallerDirectoryCleanupTarget_RejectsUnexpectedPrefix_Issue3659()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx-other-{Guid.NewGuid():N}");

        var valid = ProgramRunner.TryValidateUpgradeInstallerDirectoryCleanupTarget(
            path,
            out _,
            out var failureReason);

        Assert.False(valid);
        Assert.Contains("expected upgrade temporary-directory prefix", failureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateUpgradeInstallerDirectoryCleanupTarget_RejectsSymlink_Issue3659()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_cleanup_link_{Guid.NewGuid():N}");
        var target = Path.Combine(root, "target");
        var link = Path.Combine(Path.GetTempPath(), $"cdidx-install-link-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(link, target);

            var valid = ProgramRunner.TryValidateUpgradeInstallerDirectoryCleanupTarget(
                link,
                out _,
                out var failureReason);

            Assert.False(valid);
            Assert.Contains("symbolic link", failureReason, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void RunUpgrade_CheckOnlyJsonPrerelease_ReportsSelection()
    {
        lock (TestConsoleLock.Gate)
        {
            var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
            ProgramRunner.UpgradeHttpClientFactory = () => new HttpClient(
                new StaticResponseHandler(new ByteArrayContent(Encoding.UTF8.GetBytes("""
                    [
                      { "tag_name": "v9.9.9", "draft": false, "prerelease": false },
                      { "tag_name": "v9.9.9-rc.2", "draft": true, "prerelease": true },
                      { "tag_name": "v9.9.9-rc.1", "draft": false, "prerelease": true }
                    ]
                    """))))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            try
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["upgrade", "--check-only", "--json", "--prerelease"],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stderr);
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                Assert.Equal("v9.9.9-rc.1", root.GetProperty("latest_version").GetString());
                Assert.Equal("v9.9.9-rc.1", root.GetProperty("selected_version").GetString());
                Assert.Equal("prerelease", root.GetProperty("selected_channel").GetString());
                Assert.Equal("prerelease", root.GetProperty("selection_source").GetString());
                Assert.True(root.GetProperty("include_prerelease").GetBoolean());
            }
            finally
            {
                ProgramRunner.UpgradeHttpClientFactory = previousFactory;
            }
        }
    }

    [Fact]
    public void RunUpgrade_CheckOnlyJsonPrereleaseNotFound_ReportsStructuredFailure_Issue3453()
    {
        lock (TestConsoleLock.Gate)
        {
            var previousFactory = ProgramRunner.UpgradeHttpClientFactory;
            ProgramRunner.UpgradeHttpClientFactory = () => new HttpClient(
                new StaticResponseHandler(new ByteArrayContent(Encoding.UTF8.GetBytes("""
                    [
                      { "tag_name": "v9.9.9", "draft": false, "prerelease": false }
                    ]
                    """))))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            try
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    ["upgrade", "--check-only", "--json", "--prerelease"],
                    appVersion: "1.10.0"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Empty(stderr);
                using var doc = JsonDocument.Parse(stdout);
                var root = doc.RootElement;
                Assert.Equal("prerelease_not_found", root.GetProperty("error").GetString());
                Assert.Equal("release_metadata", root.GetProperty("error_category").GetString());
                Assert.Contains("omit --prerelease", root.GetProperty("error_hint").GetString(), StringComparison.Ordinal);
                Assert.True(root.GetProperty("include_prerelease").GetBoolean());
                Assert.False(root.GetProperty("install_attempted").GetBoolean());
            }
            finally
            {
                ProgramRunner.UpgradeHttpClientFactory = previousFactory;
            }
        }
    }

    [Theory]
    [InlineData(Architecture.X64, "CodeIndex-win-x64.zip")]
    [InlineData(Architecture.Arm64, "CodeIndex-win-arm64.zip")]
    public void CreateWindowsUpgradeHandoff_UsesNuGetVersionAndMatchingAsset(Architecture architecture, string expectedAsset)
    {
        var handoff = ProgramRunner.CreateWindowsUpgradeHandoff("v2.0.0-rc.1", architecture);

        Assert.Equal("dotnet tool update -g cdidx --version 2.0.0-rc.1", handoff.Command);
        Assert.Equal("https://github.com/Widthdom/CodeIndex/releases/tag/v2.0.0-rc.1", handoff.Url);
        Assert.Equal(expectedAsset, handoff.Asset);
        Assert.Equal(
            $"https://github.com/Widthdom/CodeIndex/releases/download/v2.0.0-rc.1/{expectedAsset}",
            handoff.AssetUrl);
    }

    [Fact]
    public async Task DownloadReleaseChecksumManifestAsync_RejectsOverLimitResponse()
    {
        using var client = new HttpClient(new StaticResponseHandler(new ByteArrayContent(new byte[(int)ProgramRunner.MaxReleaseChecksumBytes + 1])))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ProgramRunner.DownloadReleaseChecksumManifestAsync(
                client,
                "v1.27.0",
                TimeSpan.FromSeconds(1),
                CancellationToken.None));

        Assert.Contains($"{ProgramRunner.MaxReleaseChecksumBytes} byte limit", ex.Message);
    }

    [Fact]
    public async Task DownloadReleaseChecksumManifestAsync_HttpFailureUsesBoundedRedactedDiagnostics_Issue3973()
    {
        var errorBody = """
            {
              "message": "denied",
              "authorization": "Bearer secret-token-3973",
              "details": "release asset unavailable"
            }
            """;
        using var client = new HttpClient(new StaticResponseHandler(
            new StringContent(errorBody, Encoding.UTF8, "application/json"),
            HttpStatusCode.Forbidden))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            ProgramRunner.DownloadReleaseChecksumManifestAsync(
                client,
                "v1.27.0",
                TimeSpan.FromSeconds(1),
                CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("GitHub release download failed for sha256sums.txt: 403:", ex.Message);
        Assert.Contains("[redacted]", ex.Message);
        Assert.DoesNotContain("secret-token-3973", ex.Message);
        Assert.True(ex.Message.Length < 700, ex.Message);
    }

    [Fact]
    public async Task DownloadInstallerScriptAsync_UsesReleaseDownloadHeadersWithoutApiMediaType_Issue3973()
    {
        var handler = new StaticResponseHandler(new ByteArrayContent(Encoding.UTF8.GetBytes("#!/bin/sh\n")));
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var scriptPath = Path.Combine(Path.GetTempPath(), $"cdidx_install_header_{Guid.NewGuid():N}.sh");
        try
        {
            await ProgramRunner.DownloadInstallerScriptAsync(
                client,
                "v1.27.0",
                scriptPath,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.NotNull(handler.LastRequest);
            Assert.Contains(handler.LastRequest!.Headers.UserAgent, value => value.Product?.Name == "cdidx");
            Assert.Empty(handler.LastRequest.Headers.Accept);
            Assert.False(handler.LastRequest.Headers.Contains("X-GitHub-Api-Version"));
        }
        finally
        {
            TestProjectHelper.DeleteFile(scriptPath);
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
    public async Task UpdateChecker_FetchLatestReleaseTagAsync_UsesSharedGitHubHeaders_Issue3750()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"tag_name":"v1.27.0"}"""));
        var handler = new StaticResponseHandler(content);
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var tag = await UpdateChecker.FetchLatestReleaseTagAsync(client, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal("v1.27.0", tag);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains(handler.LastRequest!.Headers.UserAgent, value => value.Product?.Name == "cdidx");
        Assert.Contains(handler.LastRequest.Headers.Accept, value => value.MediaType == "application/vnd.github+json");
        Assert.True(handler.LastRequest.Headers.TryGetValues("X-GitHub-Api-Version", out var values));
        Assert.Contains("2022-11-28", values);
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
    public async Task UpdateChecker_ReadLatestPrereleaseTagAsync_SkipsDraftsAndStableReleases()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""
            [
              { "tag_name": "v3.0.0", "draft": false, "prerelease": false },
              { "tag_name": "v3.1.0-rc.2", "draft": true, "prerelease": true },
              { "tag_name": "v3.1.0-rc.1", "draft": false, "prerelease": true }
            ]
            """));

        var tag = await UpdateChecker.ReadLatestPrereleaseTagAsync(content, CancellationToken.None);

        Assert.Equal("v3.1.0-rc.1", tag);
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

    [Fact]
    public void Run_SearchBooleanOriginFilterDoesNotConsumeFollowingOptionLikeQuery_Issue3423()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_issue3423_origin_filter_option_query");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "USER_GUIDE.md",
                "markdown",
                "--log-max-size-mb appears here\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "--exclude-comments", "--log-max-size-mb", "--path", "USER_GUIDE.md", "--db", dbPath, "--count", "--exact-substring"],
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
        env.Set(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, null);
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
            Assert.Null(Environment.GetEnvironmentVariable(GlobalToolLog.LogMaxSizeMbEnvironmentVariable));
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
        env.Set(GlobalToolLog.LogMaxSizeMbEnvironmentVariable, null);
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
            Assert.Null(Environment.GetEnvironmentVariable(GlobalToolLog.LogMaxSizeMbEnvironmentVariable));
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
            Assert.Contains("exception[0] type=System.ApplicationException message=\"exception_message_redacted\"", log);
            Assert.Contains("inner_exception[1] type=System.InvalidOperationException message=\"invalid_operation\"", log);
            Assert.DoesNotContain("outer wrapper", log);
            Assert.DoesNotContain("root cause", log);
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
    public void IsTrimmedJsonUnavailable_UsesReflectionStateAndSystemTextJsonSource()
    {
        var ex = new InvalidOperationException("localized or future provider text")
        {
            Source = "System.Text.Json",
        };

        Assert.True(JsonOutputFailure.IsTrimmedJsonUnavailable(ex, reflectionEnabledByDefault: false));
        Assert.False(JsonOutputFailure.IsTrimmedJsonUnavailable(ex, reflectionEnabledByDefault: true));
        Assert.False(JsonOutputFailure.IsTrimmedJsonUnavailable(
            new InvalidOperationException("localized or future provider text") { Source = "Other.Json" },
            reflectionEnabledByDefault: false));
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

    private static void WriteFreshUpdateCheckCache(string cacheRoot, string latestTag)
    {
        var cacheDir = Path.Combine(cacheRoot, "cdidx");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(
            Path.Combine(cacheDir, "update-check.json"),
            $$"""
            {"checked_at":"{{DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}}","latest_tag":"{{latestTag}}"}
            """);
    }

    private static string ShellQuote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

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

    private static string BuildNestedJsonArray(int depth)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < depth; i++)
            builder.Append('[');
        builder.Append('0');
        for (var i = 0; i < depth; i++)
            builder.Append(']');
        return builder.ToString();
    }

    private sealed class ThrowingResolver : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            throw new InvalidOperationException("unexpected reflection resolver access");
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

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        internal FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
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
        private readonly HttpStatusCode _statusCode;

        internal HttpRequestMessage? LastRequest { get; private set; }

        internal Action<HttpResponseMessage>? ConfigureResponse { get; init; }

        internal StaticResponseHandler(HttpContent content, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _content = content;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(_statusCode) { Content = _content };
            ConfigureResponse?.Invoke(response);
            return Task.FromResult(response);
        }
    }

    private sealed class UpgradeAssetResponseHandler : HttpMessageHandler
    {
        private readonly string _checksumManifest;
        private readonly string _installerScript;
        private readonly Action<CancellationToken> _observeToken;

        internal UpgradeAssetResponseHandler(
            string checksumManifest,
            string installerScript,
            Action<CancellationToken> observeToken)
        {
            _checksumManifest = checksumManifest;
            _installerScript = installerScript;
            _observeToken = observeToken;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _observeToken(cancellationToken);
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            HttpContent content = path.EndsWith("/sha256sums.txt", StringComparison.Ordinal)
                ? new StringContent(_checksumManifest, Encoding.UTF8, "text/plain")
                : path.EndsWith("/install.sh", StringComparison.Ordinal)
                    ? new StringContent(_installerScript, Encoding.UTF8, "text/x-shellscript")
                    : new StringContent(string.Empty, Encoding.UTF8, "text/plain");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
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
