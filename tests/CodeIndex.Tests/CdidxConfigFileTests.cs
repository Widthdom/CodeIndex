using CodeIndex.Cli;
using CodeIndex.Indexer;
using CodeIndex.Mcp;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class CdidxConfigFileTests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void LoadAndApply_NoFile_NoOp()
    {
        var dir = CreateTempDir();
        try
        {
            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.False(result.Loaded);
            Assert.False(result.Failed);
            Assert.Null(result.ConfigPath);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void RunValidate_PositionalJson_ReturnsStructuredError_Issue3892()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => CdidxConfigFile.RunValidate(["extra", "--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var payload = document.RootElement;
        Assert.Equal("error", payload.GetProperty("status").GetString());
        Assert.Contains("does not accept positional arguments", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAndApply_StopsAtRepositoryBoundary_Issue3826()
    {
        var dir = CreateTempDir();
        try
        {
            var repoRoot = Path.Combine(dir, "repo");
            var nested = Path.Combine(repoRoot, "src", "app");
            Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), """{ "debug": "1" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(nested, env.Read);

            Assert.False(result.Loaded);
            Assert.False(result.Failed);
            Assert.Null(result.ConfigPath);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_MaterializesKnownKeysIntoEnvironment()
    {
        var dir = CreateTempDir();
        try
        {
            var expectedMetricsPath = Path.Combine(dir, ".cdidx", "metrics.jsonl");
            var expectedLogDir = Path.Combine(dir, ".cdidx", "logs");
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), """
                {
                  "debug": "1",
                  "metrics_path": "./.cdidx/metrics.jsonl",
                  "disable_persistent_log": true,
                  "global_tool_log_dir": "./.cdidx/logs",
                  "stale_after": "2h",
                  "suggestion_dedup_threshold": 0.75,
                  "suggestion_max_age_days": 30,
                  "suggestion_max_count": 250,
                  "indexing": {
                    "includeKinds": ["class"],
                    "excludeKinds": ["test_method", "generated_parser"],
                    "generatedCodePatterns": ["src/generated/**", "*.client.ts"],
                    "watchPendingPathLimit": 8192
                  },
                  "mcp": {
                    "tools": { "allow": ["search", "definition"], "deny": ["index"] },
                    "rate_limit": { "rps": 5, "burst": 10, "bucket_idle_seconds": 120 }
                  }
                }
                """);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Loaded);
            Assert.Null(result.Error);
            Assert.Equal("1", result.Settings["CDIDX_DEBUG"]);
            Assert.Equal(expectedMetricsPath, result.Settings["CDIDX_METRICS"]);
            Assert.Equal("1", result.Settings["CDIDX_DISABLE_PERSISTENT_LOG"]);
            Assert.Equal(expectedLogDir, result.Settings["CDIDX_GLOBAL_TOOL_LOG_DIR"]);
            Assert.Equal("2h", result.Settings["CDIDX_STALE_AFTER"]);
            Assert.Equal("0.75", result.Settings["CDIDX_SUGGESTION_DEDUP_THRESHOLD"]);
            Assert.Equal("30", result.Settings["CDIDX_SUGGESTION_MAX_AGE_DAYS"]);
            Assert.Equal("250", result.Settings["CDIDX_SUGGESTION_MAX_COUNT"]);
            Assert.Equal("class", result.Settings["CDIDX_INDEX_INCLUDE_SYMBOL_KINDS"]);
            Assert.Equal("test_method,generated_parser", result.Settings["CDIDX_INDEX_EXCLUDE_SYMBOL_KINDS"]);
            Assert.Equal("src/generated/**,*.client.ts", result.Settings[IndexCommandRunner.GeneratedCodePatternsEnvironmentVariable]);
            Assert.Equal("8192", result.Settings[IndexCommandRunner.WatchPendingPathLimitEnvironmentVariable]);
            Assert.Equal("search,definition", result.Settings["CDIDX_MCP_TOOLS_ALLOW"]);
            Assert.Equal("index", result.Settings["CDIDX_MCP_TOOLS_DENY"]);
            Assert.Equal("5", result.Settings[RateLimiterOptions.RpsEnvVar]);
            Assert.Equal("10", result.Settings[RateLimiterOptions.BurstEnvVar]);
            Assert.Equal("120", result.Settings[RateLimiterOptions.BucketIdleSecondsEnvVar]);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_Utf8BomConfigMaterializesKnownKeysIntoEnvironment()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, ".cdidxrc.json");
            var json = """{ "metrics_path": ".cdidx/bom.jsonl" }""";
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Loaded);
            Assert.Null(result.Error);
            Assert.Equal(Path.Combine(dir, ".cdidx", "bom.jsonl"), result.Settings["CDIDX_METRICS"]);
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
            var result = CdidxConfigFile.Load(Path.Combine(dir, "src"), env.Read);

            Assert.True(result.Loaded);
            Assert.EndsWith(Path.Combine(".cdidx", "config.json"), result.ConfigPath);
            Assert.Equal("41", result.Settings[QueryCommandRunner.DefaultLimitEnvironmentVariable]);
            Assert.Equal("5", result.Settings[QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable]);
            Assert.Equal("120", result.Settings[QueryCommandRunner.DefaultMaxLineWidthEnvironmentVariable]);
            Assert.EndsWith(Path.Combine(".cdidx", "config.json"), result.Sources[QueryCommandRunner.DefaultLimitEnvironmentVariable]);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_ProjectConfigJsonRejectsExcessiveJsonDepth()
    {
        var dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".cdidx"));
            var nesting = CdidxConfigFile.MaxConfigJsonDepth + 2;
            File.WriteAllText(
                Path.Combine(dir, ".cdidx", "config.json"),
                """{ "search": { "limit": """ + new string('[', nesting) + "1" + new string(']', nesting) + " } }");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("Invalid JSON", result.Error);
            Assert.Contains("depth", result.Error!.ToLowerInvariant());
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_Utf16BomConfigAboveUtf8ParseLimitReturnsInvalidJson_Issue4127()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, ".cdidxrc.json");
            var encoding = Encoding.Unicode;
            var valueLength = CdidxConfigFile.MaxConfigFileBytes / 2;
            string json;
            byte[] bytes;
            do
            {
                valueLength--;
                var value = new string('あ', valueLength);
                json = $$"""{ "debug": "{{value}}" }""";
                bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(json)];
            }
            while (bytes.Length > CdidxConfigFile.MaxConfigFileBytes);

            Assert.True(json.Length <= CdidxConfigFile.MaxConfigFileBytes);
            Assert.True(bytes.Length <= CdidxConfigFile.MaxConfigFileBytes);
            Assert.True(Encoding.UTF8.GetByteCount(json) > CdidxConfigFile.MaxConfigFileBytes);
            File.WriteAllBytes(path, bytes);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("Invalid JSON", result.Error);
            Assert.Contains("JSON payload exceeds", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_ProjectConfigJsonResolvesOutputPathFromWorkspaceRoot()
    {
        var dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".cdidx"));
            File.WriteAllText(Path.Combine(dir, ".cdidx", "config.json"), """
                {
                  "metrics_path": "./metrics.jsonl",
                  "global_tool_log_dir": "./logs"
                }
                """);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Loaded);
            Assert.Null(result.Error);
            Assert.Equal(Path.Combine(dir, "metrics.jsonl"), result.Settings["CDIDX_METRICS"]);
            Assert.Equal(Path.Combine(dir, "logs"), result.Settings["CDIDX_GLOBAL_TOOL_LOG_DIR"]);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Theory]
    [InlineData("metrics_path")]
    [InlineData("global_tool_log_dir")]
    public void LoadAndApply_OutputPathOutsideWorkspace_ReturnsError(string key)
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                $$"""{ "{{key}}": "../outside/path" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains(key, result.Error);
            Assert.Contains("config workspace root", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Theory]
    [InlineData("metrics_path", "bridge/new/metrics.jsonl")]
    [InlineData("global_tool_log_dir", "bridge/new/logs")]
    public void LoadAndApply_OutputPathUnderSymlinkAncestorIsRejectedWithoutExternalMutation_Issue5181(
        string key,
        string configuredPath)
    {
        var workspace = CreateTempDir();
        var outside = CreateTempDir();
        var bridge = Path.Combine(workspace, "bridge");
        try
        {
            if (!TryCreateDirectoryLink(bridge, outside))
                return;

            File.WriteAllText(
                Path.Combine(workspace, CdidxConfigFile.FileName),
                JsonSerializer.Serialize(new Dictionary<string, string> { [key] = configuredPath }));

            var result = CdidxConfigFile.Load(workspace, new TestEnvironment().Read);

            Assert.True(result.Failed);
            Assert.Contains(key, result.Error, StringComparison.Ordinal);
            Assert.Contains(RepositoryOutputPathBoundary.UnsafeReason, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(outside, result.Error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(outside, "new")));
            Assert.Empty(result.Settings);
        }
        finally
        {
            DeleteLinkEntry(bridge);
            TestProjectHelper.DeleteDirectory(workspace);
            TestProjectHelper.DeleteDirectory(outside);
        }
    }

    [Theory]
    [InlineData("metrics_path", false, false)]
    [InlineData("metrics_path", false, true)]
    [InlineData("global_tool_log_dir", true, false)]
    [InlineData("global_tool_log_dir", true, true)]
    public void LoadAndApply_FinalLinkIsRejectedWithoutChangingTarget_Issue5181(
        string key,
        bool directoryLink,
        bool targetExists)
    {
        var workspace = CreateTempDir();
        var outside = CreateTempDir();
        var destinationName = directoryLink ? "logs" : "metrics.jsonl";
        var destination = Path.Combine(workspace, destinationName);
        var target = Path.Combine(outside, "target");
        UnixFileMode? originalOutsideMode = null;
        UnixFileMode? originalTargetMode = null;
        try
        {
            if (targetExists)
            {
                if (directoryLink)
                    Directory.CreateDirectory(target);
                else
                    File.WriteAllText(target, "outside-target-content");
            }
            if (!OperatingSystem.IsWindows())
            {
                originalOutsideMode = File.GetUnixFileMode(outside);
                if (targetExists)
                    originalTargetMode = File.GetUnixFileMode(target);
            }
            if (!TryCreateLink(destination, target, directoryLink))
                return;

            File.WriteAllText(
                Path.Combine(workspace, CdidxConfigFile.FileName),
                JsonSerializer.Serialize(new Dictionary<string, string> { [key] = destinationName }));

            var result = CdidxConfigFile.Load(workspace, new TestEnvironment().Read);

            Assert.True(result.Failed);
            Assert.Contains(key, result.Error, StringComparison.Ordinal);
            Assert.Contains(RepositoryOutputPathBoundary.UnsafeReason, result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(outside, result.Error, StringComparison.Ordinal);
            if (targetExists && !directoryLink)
                Assert.Equal("outside-target-content", File.ReadAllText(target));
            if (!targetExists)
            {
                Assert.False(File.Exists(target));
                Assert.False(Directory.Exists(target));
            }
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(originalOutsideMode, File.GetUnixFileMode(outside));
                if (targetExists)
                    Assert.Equal(originalTargetMode, File.GetUnixFileMode(target));
            }
        }
        finally
        {
            DeleteLinkEntry(destination);
            TestProjectHelper.DeleteDirectory(workspace);
            TestProjectHelper.DeleteDirectory(outside);
        }
    }

    [Fact]
    public void LoadAndApply_WorkspaceRootAliasStillAllowsOrdinaryContainedOutput_Issue5181()
    {
        var container = CreateTempDir();
        var physicalWorkspace = Path.Combine(container, "physical");
        var workspaceAlias = Path.Combine(container, "workspace-alias");
        try
        {
            Directory.CreateDirectory(physicalWorkspace);
            if (!TryCreateDirectoryLink(workspaceAlias, physicalWorkspace))
                return;

            File.WriteAllText(
                Path.Combine(physicalWorkspace, CdidxConfigFile.FileName),
                """{ "metrics_path": "safe/metrics.jsonl" }""");

            var result = CdidxConfigFile.Load(workspaceAlias, new TestEnvironment().Read);

            Assert.True(result.Loaded);
            Assert.Null(result.Error);
            Assert.Equal(
                Path.Combine(workspaceAlias, "safe", "metrics.jsonl"),
                result.Settings[MetricsSink.EnvVarName]);
            using var environment = CdidxEnvironment.Push(result.Settings, result.Sources);
            using var session = MetricsSink.TryStartForTesting(explicitPath: null, maxBytes: 1024 * 1024);
            Assert.NotNull(session);
            MetricsSink.Record(new MetricsEvent(
                Timestamp: DateTimeOffset.UtcNow,
                Tool: "status",
                Source: "cli",
                ElapsedMs: 1,
                ExitCode: 0));
            Assert.True(session.WaitForIdle(TimeSpan.FromSeconds(5)));
            Assert.Contains(
                "\"tool\":\"status\"",
                File.ReadAllText(Path.Combine(physicalWorkspace, "safe", "metrics.jsonl")),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteLinkEntry(workspaceAlias);
            TestProjectHelper.DeleteDirectory(container);
        }
    }

    [Fact]
    public void OutputBoundary_RejectsCaseOnlySiblingInDistinctParentNamespace_Issue5181Review()
    {
        var root = CreateTempDir();
        var upperWorkspace = Path.Combine(root, "Workspace");
        var lowerWorkspace = Path.Combine(root, "workspace");
        var configuredPath = Path.Combine(lowerWorkspace, "logs", "metrics.jsonl");
        var rootIdentity = new FileIndexer.FileIdentity(1, 1);
        var upperIdentity = new FileIndexer.FileIdentity(2, 2);
        var lowerIdentity = new FileIndexer.FileIdentity(3, 3);
        try
        {
            Directory.CreateDirectory(upperWorkspace);
            FileIndexer.FileIdentity? Identity(string path)
            {
                var fullPath = Path.GetFullPath(path);
                if (string.Equals(fullPath, root, StringComparison.Ordinal))
                    return rootIdentity;
                if (string.Equals(fullPath, upperWorkspace, StringComparison.Ordinal))
                    return upperIdentity;
                if (string.Equals(fullPath, lowerWorkspace, StringComparison.Ordinal))
                    return lowerIdentity;
                return null;
            }

            bool IgnoreCase(string path)
                => !string.Equals(Path.GetFullPath(path), root, StringComparison.Ordinal);

            RepositoryOutputPathBoundary.ContainsPathForTesting = (parent, child) =>
                PathCasing.IsPathEqualOrParentByDirectoryNamespaceForTesting(
                    parent,
                    child,
                    IgnoreCase,
                    Identity);

            var accepted = RepositoryOutputPathBoundary.TryResolveConfiguredPath(
                configuredPath,
                upperWorkspace,
                destinationIsDirectory: false,
                out _,
                out var failureReason);

            Assert.False(accepted);
            Assert.Equal("outside_workspace", failureReason);
        }
        finally
        {
            RepositoryOutputPathBoundary.ContainsPathForTesting = null;
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void RepositoryOutputBoundary_RevalidatesAfterInjectedAncestorSwap_Issue5181()
    {
        var workspace = CreateTempDir();
        var outside = CreateTempDir();
        var safeDirectory = Path.Combine(workspace, "safe");
        var configuredPath = Path.Combine(safeDirectory, "new", "metrics.jsonl");
        try
        {
            Directory.CreateDirectory(safeDirectory);
            File.WriteAllText(
                Path.Combine(workspace, CdidxConfigFile.FileName),
                """{ "metrics_path": "safe/new/metrics.jsonl" }""");
            var result = CdidxConfigFile.Load(workspace, new TestEnvironment().Read);
            Assert.True(result.Loaded);

            using var environment = CdidxEnvironment.Push(result.Settings, result.Sources);
            var boundary = RepositoryOutputPathBoundary.CreateGuardForConfigSource(
                MetricsSink.EnvVarName,
                "metrics_path",
                configuredPath,
                destinationIsDirectory: false);
            Assert.NotNull(boundary);

            var swapped = false;
            RepositoryOutputPathBoundary.BeforeMutationForTesting = (operation, _) =>
            {
                if (swapped || operation != "create_directory")
                    return;
                swapped = true;
                Directory.Delete(safeDirectory);
                Directory.CreateSymbolicLink(safeDirectory, outside);
            };

            var exception = Assert.Throws<RepositoryOutputPathBoundaryException>(
                () => boundary!.CreateSensitiveDestinationDirectory());

            Assert.Contains(RepositoryOutputPathBoundary.UnsafeReason, exception.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(outside, "new")));
            Assert.False(File.Exists(Path.Combine(outside, "new", "metrics.jsonl")));
        }
        finally
        {
            RepositoryOutputPathBoundary.BeforeMutationForTesting = null;
            DeleteLinkEntry(safeDirectory);
            TestProjectHelper.DeleteDirectory(workspace);
            TestProjectHelper.DeleteDirectory(outside);
        }
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains(expectedError, result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Theory]
    [InlineData("""{ "indexing": { "watchPendingPathLimit": 0 } }""", "positive integer")]
    [InlineData("""{ "indexing": { "watchPendingPathLimit": 262145 } }""", "<= 262144")]
    [InlineData("""{ "indexing": { "watchPendingPathLimit": 1.5 } }""", "positive integer")]
    public void LoadAndApply_ProjectConfigJsonRejectsInvalidWatchPendingPathLimit(string json, string expectedError)
    {
        var dir = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".cdidx"));
            File.WriteAllText(Path.Combine(dir, ".cdidx", "config.json"), json);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains(expectedError, result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Theory]
    [InlineData("""{ "suggestion_max_age_days": 1.5 }""", "suggestion_max_age_days", "positive integer")]
    [InlineData("""{ "suggestion_max_age_days": 2147483648 }""", "suggestion_max_age_days", "positive integer")]
    [InlineData("""{ "suggestion_max_count": 1.5 }""", "suggestion_max_count", "positive integer")]
    public void LoadAndApply_InvalidSuggestionIntegerConfig_ReturnsError_Issue3697(string json, string expectedKey, string expectedError)
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), json);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains(expectedKey, result.Error);
            Assert.Contains(expectedError, result.Error);
            Assert.Empty(result.Settings);
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
                """{ "metrics_path": "./.cdidx/config.jsonl" }""");

            var env = new TestEnvironment(initial: new() { ["CDIDX_METRICS"] = "/from/env.jsonl" });
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Loaded);
            Assert.False(result.Settings.ContainsKey("CDIDX_METRICS"));
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
                """{ "metrics_path": "./.cdidx/config.jsonl" }""");

            var env = new TestEnvironment(initial: new() { ["CDIDX_METRICS"] = "" });
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Loaded);
            Assert.False(result.Settings.ContainsKey("CDIDX_METRICS"));
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
            var result = CdidxConfigFile.Load(nested, env.Read);

            Assert.True(result.Loaded);
            Assert.Equal(Path.Combine(root, ".cdidxrc.json"), result.ConfigPath);
            Assert.Equal("config-value", result.Settings["CDIDX_DEBUG"]);
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.False(result.Loaded);
            Assert.False(result.Failed);
            Assert.Empty(result.Settings);
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("Invalid JSON", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_ExcessiveJsonDepth_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            var nesting = CdidxConfigFile.MaxConfigJsonDepth + 2;
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                """{ "debug": """ + new string('[', nesting) + """"1"""" + new string(']', nesting) + " }");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("Invalid JSON", result.Error);
            Assert.Contains("depth", result.Error!.ToLowerInvariant());
            Assert.Empty(result.Settings);
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("github_token", result.Error);
            Assert.Empty(result.Settings);
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("mcp.tools.bogus", result.Error);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_InvalidConfigReportsMultipleDiagnostics_Issue3432()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), """
                {
                  "debug": "1",
                  "github_token": "secret",
                  "disable_persistent_log": "yes",
                  "search": {
                    "limit": 0,
                    "typo": true
                  },
                  "mcp": {
                    "rate_limit": {
                      "burst": "fast",
                      "unknown": 1
                    }
                  }
                }
                """);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("github_token", result.Error);
            Assert.Contains("disable_persistent_log", result.Error);
            Assert.Contains("search.limit", result.Error);
            Assert.Contains("search.typo", result.Error);
            Assert.Contains("mcp.rate_limit.burst", result.Error);
            Assert.Contains("mcp.rate_limit.unknown", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_StringArrayAboveMaximumItemCount_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            var items = string.Join(
                ",",
                Enumerable.Range(0, CdidxConfigFile.MaxConfigStringArrayItems + 1).Select(i => $"\"kind{i}\""));
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                $$"""{ "indexing": { "includeKinds": [{{items}}] } }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("indexing.includeKinds", result.Error);
            Assert.Contains($"<= {CdidxConfigFile.MaxConfigStringArrayItems} items", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_StringArrayItemAboveMaximumLength_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            var item = new string('x', CdidxConfigFile.MaxConfigStringArrayItemChars + 1);
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                $$"""{ "mcp": { "tools": { "allow": ["{{item}}"] } } }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("mcp.tools.allow", result.Error);
            Assert.Contains($"<= {CdidxConfigFile.MaxConfigStringArrayItemChars} characters", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_ScalarStringAboveMaximumLength_ReturnsError_Issue3431()
    {
        var dir = CreateTempDir();
        try
        {
            var value = new string('x', CdidxConfigFile.MaxConfigScalarStringChars + 1);
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                $$"""{ "debug": "{{value}}" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("debug", result.Error);
            Assert.Contains($"<= {CdidxConfigFile.MaxConfigScalarStringChars} characters", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_PathStringAboveMaximumLength_ReturnsError_Issue3431()
    {
        var dir = CreateTempDir();
        const string Sentinel = "PATH_LENGTH_SENTINEL_3431";
        try
        {
            var value = new string('x', CdidxConfigFile.MaxConfigPathStringChars + 1) + Sentinel;
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                $$"""{ "metrics_path": "{{value}}" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("metrics_path", result.Error);
            Assert.Contains($"<= {CdidxConfigFile.MaxConfigPathStringChars} characters", result.Error);
            Assert.DoesNotContain(Sentinel, result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_InvalidOutputPathUsesSanitizedDiagnostic_Issue3431()
    {
        var dir = CreateTempDir();
        const string Sentinel = "PATH_EXCEPTION_SENTINEL_3431";
        try
        {
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                $$"""{ "metrics_path": "{{Sentinel}}\u0000.txt" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("metrics_path", result.Error);
            Assert.Contains("invalid_path", result.Error);
            Assert.DoesNotContain(Sentinel, result.Error);
            Assert.Empty(result.Settings);
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("must be a boolean", result.Error);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Theory]
    [InlineData("""{ "mcp": { "rate_limit": { "rps": 0 } } }""", "mcp.rate_limit.rps")]
    [InlineData("""{ "mcp": { "rate_limit": { "bucket_idle_seconds": 1e9999 } } }""", "mcp.rate_limit.bucket_idle_seconds")]
    public void LoadAndApply_InvalidMcpRateLimitNumber_ReturnsError_Issue3431(string json, string expectedKey)
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), json);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains(expectedKey, result.Error);
            Assert.Contains("finite", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_McpRateLimitAboveMaximum_ReturnsError_Issue3431()
    {
        var dir = CreateTempDir();
        try
        {
            var rps = RateLimiterOptions.MaxRefillTokensPerSecond + 1;
            var burst = RateLimiterOptions.MaxBurstCapacity + 1;
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), $$"""
                {
                  "mcp": {
                    "rate_limit": {
                      "rps": {{rps.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                      "burst": {{burst.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
                    }
                  }
                }
                """);

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("mcp.rate_limit.rps", result.Error);
            Assert.Contains(RateLimiterOptions.MaxRefillTokensPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Error);
            Assert.Contains("mcp.rate_limit.burst", result.Error);
            Assert.Contains(RateLimiterOptions.MaxBurstCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Error);
            Assert.Empty(result.Settings);
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("suggestion_dedup_threshold", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_SuggestionMaxAgeAboveMaximum_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            var tooLarge = SuggestionStore.MaximumMaxAgeDays + 1;
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                $$"""{ "suggestion_max_age_days": {{tooLarge}} }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("suggestion_max_age_days", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_SuggestionMaxCountAboveMaximum_ReturnsError()
    {
        var dir = CreateTempDir();
        try
        {
            var tooLarge = SuggestionStore.MaximumMaxCount + 1;
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"),
                $$"""{ "suggestion_max_count": {{tooLarge}} }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("suggestion_max_count", result.Error);
            Assert.Empty(result.Settings);
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Loaded);
            Assert.Null(result.Error);
            Assert.Equal("1", result.Settings["CDIDX_DEBUG"]);
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
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Loaded);
            Assert.False(result.Settings.ContainsKey("CDIDX_DISABLE_PERSISTENT_LOG"));
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

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["definitely-not-a-command"],
                appVersion: "1.21.0",
                configStartDirectory: dir));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Empty(stdout);
            Assert.Contains($"Error [{CommandErrorCodes.ConfigInvalid}]", stderr);
            Assert.Contains("Invalid JSON", stderr);
            Assert.Contains("CDIDX_DISABLE_CONFIG_FILE", stderr);
            Assert.Contains("Usage:", stderr);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void Run_StaticCommandsIgnoreMalformedSupportedConfigs_Issue4886()
    {
        var root = CreateTempDir();
        try
        {
            var configRelativePaths = new[]
            {
                CdidxConfigFile.ProjectConfigRelativePath,
                CdidxConfigFile.FileName,
            };
            var commandCases = new (string Name, string[] Args)[]
            {
                ("license", ["license", "--json"]),
                ("version_with_global_flag", ["--quiet", "--version", "--json"]),
                ("help", ["help", "status"]),
                ("subcommand_help", ["index", "--help"]),
                ("validate_config_help", ["validate-config", "--help"]),
                ("config_show_help", ["config", "show", "--help"]),
                ("completions", ["completions", "bash"]),
            };

            for (var configIndex = 0; configIndex < configRelativePaths.Length; configIndex++)
            {
                var project = Path.Combine(root, $"project-{configIndex}");
                Directory.CreateDirectory(project);
                var configPath = Path.Combine(project, configRelativePaths[configIndex]);
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, "{ not-json");

                foreach (var commandCase in commandCases)
                {
                    var result = CaptureConsole(() => ProgramRunner.Run(
                        commandCase.Args,
                        appVersion: "1.40.3",
                        configStartDirectory: project));

                    Assert.True(
                        result.ExitCode == CommandExitCodes.Success,
                        $"{commandCase.Name} was blocked by {configRelativePaths[configIndex]}: {result.Stderr}");
                    Assert.DoesNotContain("Invalid JSON", result.Stdout, StringComparison.Ordinal);
                    Assert.DoesNotContain("Invalid JSON", result.Stderr, StringComparison.Ordinal);
                }
            }
        }
        finally { TestProjectHelper.DeleteDirectory(root); }
    }

    [Fact]
    public void Run_ConfigDependentCommandsReturnTypedJsonForMalformedConfig_Issue4886()
    {
        var dir = CreateTempDir();
        try
        {
            var configPath = Path.Combine(dir, CdidxConfigFile.ProjectConfigRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, "{ not-json");

            var commandCases = new (string Command, string[] Args)[]
            {
                ("index", ["index", dir, "--json"]),
                ("search", ["search", "needle", "--json"]),
                ("search", ["search", "needle", "--format", "json"]),
                ("config", ["config", "unknown", "--json"]),
            };

            foreach (var commandCase in commandCases)
            {
                var result = CaptureConsole(() => ProgramRunner.Run(
                    commandCase.Args,
                    appVersion: "1.40.3",
                    configStartDirectory: dir));

                Assert.Equal(CommandExitCodes.UsageError, result.ExitCode);
                Assert.Empty(result.Stderr);
                using var document = JsonDocument.Parse(result.Stdout);
                var payload = document.RootElement;
                Assert.Equal("1", payload.GetProperty("api_version").GetString());
                Assert.Equal("error", payload.GetProperty("status").GetString());
                Assert.Equal(CommandErrorCodes.ConfigInvalid, payload.GetProperty("error_code").GetString());
                Assert.Equal("configuration", payload.GetProperty("category").GetString());
                Assert.Equal(commandCase.Command, payload.GetProperty("command").GetString());
                Assert.Equal(CommandExitCodes.UsageError, payload.GetProperty("exit_code").GetInt32());
                Assert.Contains("Invalid JSON", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
                Assert.Contains(
                    CdidxConfigFile.DisableEnvVar,
                    payload.GetProperty("hint").GetString(),
                    StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("usage").GetString()));
            }

            var optionValueResult = CaptureConsole(() => ProgramRunner.Run(
                ["search", "--query", "--json"],
                appVersion: "1.40.3",
                configStartDirectory: dir));

            Assert.Equal(CommandExitCodes.UsageError, optionValueResult.ExitCode);
            Assert.Empty(optionValueResult.Stdout);
            Assert.Contains($"Error [{CommandErrorCodes.ConfigInvalid}]", optionValueResult.Stderr);

            var globalOptionValueResult = CaptureConsole(() => ProgramRunner.Run(
                ["--metrics", "--json", "search", "needle"],
                appVersion: "1.40.3",
                configStartDirectory: dir));

            Assert.Equal(CommandExitCodes.UsageError, globalOptionValueResult.ExitCode);
            Assert.Empty(globalOptionValueResult.Stdout);
            Assert.Contains($"Error [{CommandErrorCodes.ConfigInvalid}]", globalOptionValueResult.Stderr);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_OversizedConfigFile_FailsBeforeParsing()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                new string('x', CdidxConfigFile.MaxConfigFileBytes + 1));

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains($"{CdidxConfigFile.MaxConfigFileBytes} byte limit", result.Error);
            Assert.Empty(result.Settings);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_ManyUnknownKeysCapsDiagnostics_Issue3793()
    {
        var dir = CreateTempDir();
        try
        {
            var unknownKeys = Enumerable
                .Range(0, CdidxConfigFile.MaxUnknownKeyDiagnostics + 4)
                .Select(index => $"\"unknown_key_{index}\": true");
            File.WriteAllText(
                Path.Combine(dir, ".cdidxrc.json"),
                "{" + string.Join(",", unknownKeys) + "}");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            for (var i = 0; i < CdidxConfigFile.MaxUnknownKeyDiagnostics; i++)
                Assert.Contains($"unknown_key_{i}", result.Error);
            Assert.DoesNotContain($"unknown_key_{CdidxConfigFile.MaxUnknownKeyDiagnostics}", result.Error);
            Assert.Contains($"unknown_key_count={CdidxConfigFile.MaxUnknownKeyDiagnostics + 4}", result.Error);
            Assert.Contains($"unknown_key_reported={CdidxConfigFile.MaxUnknownKeyDiagnostics}", result.Error);
            Assert.Contains($"unknown_key_limit={CdidxConfigFile.MaxUnknownKeyDiagnostics}", result.Error);
        }
        finally { TestProjectHelper.DeleteDirectory(dir); }
    }

    [Fact]
    public void LoadAndApply_InvalidJsonSanitizesLongConfigPath_Issue3793()
    {
        var root = CreateTempDir();
        var sensitiveSegment = "secret-config-token-ghp_1234567890abcdef-private";
        var dir = Path.Combine(root, sensitiveSegment, new string('a', 80));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), "{ invalid");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("Invalid JSON", result.Error);
            Assert.DoesNotContain(dir, result.Error);
            Assert.DoesNotContain(sensitiveSegment, result.Error);
            Assert.DoesNotContain("ghp_1234567890abcdef", result.Error);
            Assert.True(result.Error!.Length < 600);
        }
        finally { TestProjectHelper.DeleteDirectory(root); }
    }

    [Fact]
    public void LoadAndApply_InvalidTypeSanitizesConfigPath_Issue3793()
    {
        var root = CreateTempDir();
        var sensitiveSegment = "secret-config-token-ghp_1234567890abcdef-private";
        var dir = Path.Combine(root, sensitiveSegment);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), """{ "debug": 1 }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("`debug` must be a string", result.Error);
            Assert.DoesNotContain(dir, result.Error);
            Assert.DoesNotContain(root, result.Error);
            Assert.DoesNotContain(sensitiveSegment, result.Error);
            Assert.DoesNotContain("ghp_1234567890abcdef", result.Error);
        }
        finally { TestProjectHelper.DeleteDirectory(root); }
    }

    [Fact]
    public void LoadAndApply_WorkspaceOutputPathErrorSanitizesConfigRoot_Issue3793()
    {
        var root = CreateTempDir();
        var sensitiveSegment = "secret-config-token-ghp_1234567890abcdef-private";
        var dir = Path.Combine(root, sensitiveSegment);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), """{ "metrics_path": "../outside/metrics.jsonl" }""");

            var env = new TestEnvironment();
            var result = CdidxConfigFile.Load(dir, env.Read);

            Assert.True(result.Failed);
            Assert.Contains("config workspace root", result.Error);
            Assert.DoesNotContain(dir, result.Error);
            Assert.DoesNotContain(root, result.Error);
            Assert.DoesNotContain(sensitiveSegment, result.Error);
            Assert.DoesNotContain("ghp_1234567890abcdef", result.Error);
        }
        finally { TestProjectHelper.DeleteDirectory(root); }
    }

    private static string CreateTempDir()
    {
        return TestProjectHelper.CreateTempProject("cdidx_config");
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
        => TryCreateLink(linkPath, targetPath, directoryLink: true);

    private static bool TryCreateLink(string linkPath, string targetPath, bool directoryLink)
    {
        try
        {
            if (directoryLink)
                Directory.CreateSymbolicLink(linkPath, targetPath);
            else
                File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static void DeleteLinkEntry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
                return;
            if ((attributes & FileAttributes.Directory) != 0)
                Directory.Delete(path);
            else
                File.Delete(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
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

[Collection("SQLite pool sensitive")]
public sealed class CdidxConfigProcessStateTests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void RunValidate_NoConfigJson_ReturnsExplicitNotFoundStatus_Issue4320()
    {
        var dir = TestProjectHelper.CreateTempProject("cdidx_config_no_active");
        var previous = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            Environment.CurrentDirectory = dir;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => CdidxConfigFile.RunValidate(["--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.Equal("1", payload.GetProperty("api_version").GetString());
            Assert.True(payload.GetProperty("valid").GetBoolean());
            Assert.Equal(JsonValueKind.Null, payload.GetProperty("path").ValueKind);
            Assert.Equal("not_found", payload.GetProperty("status").GetString());
            Assert.Equal("no supported config file was found", payload.GetProperty("reason").GetString());
            Assert.False(payload.GetProperty("config_file_found").GetBoolean());
            Assert.False(payload.GetProperty("validated").GetBoolean());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void RunValidate_InvalidConfigJson_ReturnsStructuredErrorThroughProgramRunner_Issue3892()
    {
        var dir = TestProjectHelper.CreateTempProject("cdidx_config_invalid");
        var previous = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(dir, CdidxConfigFile.FileName), "{ invalid json");
            Environment.CurrentDirectory = dir;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["validate-config", "--json"], _jsonOptions, appVersion: "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Contains("Invalid JSON", document.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Run_SelfManagedCommandsApplyValidConfigBeforeDispatch_Issue4886()
    {
        var dir = TestProjectHelper.CreateTempProject("cdidx_config_self_managed_4886");
        var previous = Environment.CurrentDirectory;
        var sourceEnvName = CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + MetricsSink.EnvVarName;
        using var env = EnvironmentVariableScope.Capture(
            MetricsSink.EnvVarName,
            sourceEnvName,
            CdidxConfigFile.DisableEnvVar);
        env.Set(MetricsSink.EnvVarName, null);
        env.Set(sourceEnvName, null);
        env.Set(CdidxConfigFile.DisableEnvVar, null);
        try
        {
            var metricsPath = Path.Combine(dir, "metrics.jsonl");
            File.WriteAllText(
                Path.Combine(dir, CdidxConfigFile.FileName),
                """{ "metrics_path": "./metrics.jsonl" }""");
            Environment.CurrentDirectory = dir;

            var validateResult = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["validate-config", "--json"],
                _jsonOptions,
                appVersion: "test"));
            var showResult = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["config", "show", "--json"],
                _jsonOptions,
                appVersion: "test"));

            Assert.Equal(CommandExitCodes.Success, validateResult.ExitCode);
            Assert.Equal(CommandExitCodes.Success, showResult.ExitCode);
            Assert.Empty(validateResult.Stderr);
            Assert.Empty(showResult.Stderr);
            var metrics = File.ReadAllLines(metricsPath);
            Assert.Contains(metrics, line => line.Contains("\"tool\":\"validate-config\"", StringComparison.Ordinal));
            Assert.Contains(metrics, line => line.Contains("\"tool\":\"config\"", StringComparison.Ordinal));

            var metricsCountBeforeHelp = metrics.Length;
            var validateHelpResult = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["validate-config", "--help"],
                _jsonOptions,
                appVersion: "test"));
            var showHelpResult = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["config", "show", "--help"],
                _jsonOptions,
                appVersion: "test"));

            Assert.Equal(CommandExitCodes.Success, validateHelpResult.ExitCode);
            Assert.Equal(CommandExitCodes.Success, showHelpResult.ExitCode);
            Assert.Equal(metricsCountBeforeHelp, File.ReadAllLines(metricsPath).Length);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Run_MalformedConfigRoutingHonorsNestedGlobalFlagsAndCommandIdentity_Issue4886()
    {
        var dir = TestProjectHelper.CreateTempProject("cdidx_config_routing_4886");
        var previous = Environment.CurrentDirectory;
        using var env = EnvironmentVariableScope.Capture(CdidxConfigFile.DisableEnvVar);
        env.Set(CdidxConfigFile.DisableEnvVar, null);
        try
        {
            File.WriteAllText(Path.Combine(dir, CdidxConfigFile.FileName), "{ invalid json");
            Directory.CreateDirectory(Path.Combine(dir, "search"));
            Environment.CurrentDirectory = dir;

            var showResult = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["config", "--quiet", "show", "--json"],
                _jsonOptions,
                appVersion: "test"));
            var searchResult = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["search", "needle", "--json"],
                _jsonOptions,
                appVersion: "test"));

            Assert.Equal(CommandExitCodes.Success, showResult.ExitCode);
            Assert.Empty(showResult.Stderr);
            using (var showDocument = JsonDocument.Parse(showResult.Stdout))
                Assert.Equal("invalid", showDocument.RootElement.GetProperty("config_file").GetProperty("status").GetString());

            Assert.Equal(CommandExitCodes.UsageError, searchResult.ExitCode);
            Assert.Empty(searchResult.Stderr);
            using var searchDocument = JsonDocument.Parse(searchResult.Stdout);
            Assert.Equal("search", searchDocument.RootElement.GetProperty("command").GetString());
            Assert.StartsWith("cdidx search ", searchDocument.RootElement.GetProperty("usage").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Run_ConfigFileAppliesScopedDefaultsWithoutMutatingEnvironment()
    {
        var dir = TestProjectHelper.CreateTempProject("cdidx_config_scoped_defaults");
        var sourceEnvName = CdidxConfigFile.ConfigSourceEnvironmentVariablePrefix + QueryCommandRunner.DefaultLimitEnvironmentVariable;
        using var env = EnvironmentVariableScope.Capture(QueryCommandRunner.DefaultLimitEnvironmentVariable, sourceEnvName);
        env.Set(QueryCommandRunner.DefaultLimitEnvironmentVariable, null);
        env.Set(sourceEnvName, null);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".cdidxrc.json"), """{ "search": { "limit": 17 } }""");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["status", "--config", "--json"],
                appVersion: "1.30.0",
                configStartDirectory: dir));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
            using var document = JsonDocument.Parse(stdout);
            var limit = document.RootElement.GetProperty("effective_config").GetProperty("limit");
            Assert.Equal(17, limit.GetProperty("value").GetInt32());
            Assert.StartsWith("config:", limit.GetProperty("source").GetString(), StringComparison.Ordinal);
            Assert.Null(Environment.GetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable));
            Assert.Null(Environment.GetEnvironmentVariable(sourceEnvName));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(dir);
        }
    }
}
