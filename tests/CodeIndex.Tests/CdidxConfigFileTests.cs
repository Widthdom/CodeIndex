using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class CdidxConfigFileTests
{
    [Fact]
    public void LoadAndApply_NoFile_NoOp()
    {
        var dir = CreateTempDir();
        try
        {
            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.False(result.Loaded);
            Assert.False(result.Failed);
            Assert.Null(result.Path);
            Assert.Empty(env.Writes);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_MaterializesKnownKeysIntoEnvironment()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), """
                {
                  "debug": "1",
                  "metrics_path": "/tmp/m.jsonl",
                  "disable_persistent_log": true,
                  "global_tool_log_dir": "/tmp/logs",
                  "stale_after": "2h",
                  "suggestion_dedup_threshold": 0.75,
                  "suggestion_max_age_days": 30,
                  "suggestion_max_count": 250,
                  "indexing": {
                    "includeKinds": ["class"],
                    "excludeKinds": ["test_method", "generated_parser"]
                  },
                  "mcp": {
                    "tools": { "allow": ["search", "definition"], "deny": ["index"] },
                    "rate_limit": { "rps": 5, "burst": 10 }
                  }
                }
                """);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Loaded);
            Assert.Null(result.Error);
            Assert.Equal("1", env.Writes["CDIDX_DEBUG"]);
            Assert.Equal("/tmp/m.jsonl", env.Writes["CDIDX_METRICS"]);
            Assert.Equal("1", env.Writes["CDIDX_DISABLE_PERSISTENT_LOG"]);
            Assert.Equal("/tmp/logs", env.Writes["CDIDX_GLOBAL_TOOL_LOG_DIR"]);
            Assert.Equal("2h", env.Writes["CDIDX_STALE_AFTER"]);
            Assert.Equal("0.75", env.Writes["CDIDX_SUGGESTION_DEDUP_THRESHOLD"]);
            Assert.Equal("30", env.Writes["CDIDX_SUGGESTION_MAX_AGE_DAYS"]);
            Assert.Equal("250", env.Writes["CDIDX_SUGGESTION_MAX_COUNT"]);
            Assert.Equal("class", env.Writes["CDIDX_INDEX_INCLUDE_SYMBOL_KINDS"]);
            Assert.Equal("test_method,generated_parser", env.Writes["CDIDX_INDEX_EXCLUDE_SYMBOL_KINDS"]);
            Assert.Equal("search,definition", env.Writes["CDIDX_MCP_TOOLS_ALLOW"]);
            Assert.Equal("index", env.Writes["CDIDX_MCP_TOOLS_DENY"]);
            Assert.Equal("5", env.Writes["CDIDX_MCP_RATE_LIMIT_RPS"]);
            Assert.Equal("10", env.Writes["CDIDX_MCP_RATE_LIMIT_BURST"]);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_ProjectConfigJsonMaterializesSearchDefaults()
    {
        var dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".cdidx"));
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, ".cdidx", "config.json"), """
                {
                  "$schema": "https://example.invalid/cdidx.schema.json",
                  "search": {
                    "limit": 41,
                    "snippet_lines": 5,
                    "max_line_width": 120
                  },
                  "output": { "format": "json", "locale": "en" },
                  "graph": { "max_hops": 4 },
                  "folding": { "fold_key_version": 1 }
                }
                """);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(Path.Combine(dir, "src"), env.Read, env.Write);

            Assert.True(result.Loaded);
            Assert.EndsWith(Path.Combine(".cdidx", "config.json"), result.Path);
            Assert.Equal("41", env.Writes[QueryCommandRunner.DefaultLimitEnvironmentVariable]);
            Assert.Equal("5", env.Writes[QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable]);
            Assert.Equal("120", env.Writes[QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable]);
            Assert.EndsWith(Path.Combine(".cdidx", "config.json"), env.Writes[CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + QueryCommandRunner.DefaultLimitEnvironmentVariable]);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Theory]
    [InlineData("""{ "search": { "limit": 0 } }""", "positive integer")]
    [InlineData("""{ "search": { "snippet_lines": -1 } }""", "positive integer")]
    [InlineData("""{ "search": { "max_line_width": -1 } }""", "non-negative integer")]
    [InlineData("""{ "search": { "limit": 1.5 } }""", "positive integer")]
    [InlineData("""{ "search": { "limit": 10001 } }""", "<= 10000")]
    public void LoadAndApply_ProjectConfigJsonRejectsInvalidSearchDefaults(string json, string expectedError)
    {
        var dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".cdidx"));
            File.WriteAllText(Path.Combine(dir, ".cdidx", "config.json"), json);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Failed);
            Assert.Contains(expectedError, result.Error);
            Assert.Empty(env.Writes);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_RealEnvVarWinsOverConfigFile()
    {
        // Precedence contract: CLI > env > config file > defaults. The loader must NOT
        // overwrite a value the user has already set in the process environment.
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                """{ "metrics_path": "/from/config.jsonl" }""");

            var env = new TestEnvironment(initial: new() { ["CDIDX_METRICS"] = "/from/env.jsonl" });
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Loaded);
            Assert.False(env.Writes.ContainsKey("CDIDX_METRICS"));
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_EmptyEnvVarStillCountsAsSet()
    {
        // Empty-string env vars are not "unset" — RateLimiterOptions.FromEnvironment and
        // similar consumers treat empty as "feature off", so an explicit `export FOO=`
        // must defeat a checked-in config value (real env wins, per documented precedence).
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                """{ "metrics_path": "/from/config.jsonl" }""");

            var env = new TestEnvironment(initial: new() { ["CDIDX_METRICS"] = "" });
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Loaded);
            Assert.False(env.Writes.ContainsKey("CDIDX_METRICS"));
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_WalksUpwardFromStartingDirectory()
    {
        var root = CreateTempDir();
        try
        {
            var nested = Path.Combine(root, "a", "b", "c");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(root, ".cdidxrc.json"),
                """{ "debug": "config-value" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(nested, env.Read, env.Write);

            Assert.True(result.Loaded);
            Assert.Equal(Path.Combine(root, ".cdidxrc.json"), result.Path);
            Assert.Equal("config-value", env.Writes["CDIDX_DEBUG"]);
        }
        finally { TestProjectHelper.DeleteDirectory(root); }
    }

    [Fact]
    public void LoadAndApply_DisabledByEnvVar_SkipsLoad()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                """{ "debug": "1" }""");

            var env = new TestEnvironment(initial: new() { ["CDIDX_DISABLE_CONFIG_FILE"] = "1" });
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.False(result.Loaded);
            Assert.False(result.Failed);
            Assert.Empty(env.Writes);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_MalformedJson_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), "{ not-json");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Failed);
            Assert.Contains("Invalid JSON", result.Error);
            Assert.Empty(env.Writes);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_UnknownTopLevelKey_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                """{ "github_token": "secret" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Failed);
            Assert.Contains("github_token", result.Error);
            Assert.Empty(env.Writes);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_UnknownNestedMcpKey_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                """{ "mcp": { "tools": { "bogus": [] } } }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Failed);
            Assert.Contains("mcp.tools.bogus", result.Error);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_WrongType_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                """{ "disable_persistent_log": "yes" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Failed);
            Assert.Contains("must be a boolean", result.Error);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_InvalidSuggestionDedupThreshold_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                """{ "suggestion_dedup_threshold": 1.5 }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Failed);
            Assert.Contains("suggestion_dedup_threshold", result.Error);
            Assert.Empty(env.Writes);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_AllowsSchemaKeyAndComments()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), """
                {
                  // editor link
                  "$schema": "https://example/cdidxrc.schema.json",
                  "debug": "1",
                }
                """);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Loaded);
            Assert.Null(result.Error);
            Assert.Equal("1", env.Writes["CDIDX_DEBUG"]);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_DisabledPersistentLog_FalseIsNoOp()
    {
        // When `disable_persistent_log: false`, the loader must NOT set the env var:
        // absence already means "logging enabled" (the historical default). Writing "0"
        // would behave the same as "1" because `GlobalToolLog.ShouldEnable` only treats
        // exact "1" as disable, but a future change could broaden that check, so we
        // assert the contract holds today.
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                """{ "disable_persistent_log": false }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.LoadAndApply(dir, env.Read, env.Write);

            Assert.True(result.Loaded);
            Assert.False(env.Writes.ContainsKey("CDIDX_DISABLE_PERSISTENT_LOG"));
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void Run_MalformedConfigFile_FailsWithUsageError()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), "{ not-json");

            var (exitCode, _, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command"],
                appVersion: "1.21.0",
                configStartDirectory: dir));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("Invalid JSON", stderr);
            Assert.Contains("CDIDX_DISABLE_CONFIG_FILE", stderr);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);

    private sealed class TestEnvironment
    {
        private readonly Dictionary<string, string?> _env;
        public Dictionary<string, string?> Writes { get; } = new(StringComparer.Ordinal);

        public TestEnvironment(Dictionary<string, string?>? initial = null)
        {
            _env = new(initial ?? new(), StringComparer.Ordinal);
        }

        public string? Read(string name) => _env.TryGetValue(name, out var v) ? v : null;

        public void Write(string name, string? value)
        {
            _env[name] = value;
            Writes[name] = value;
        }
    }
}
