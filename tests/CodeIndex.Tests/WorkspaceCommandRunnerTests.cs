using CodeIndex.Cli;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class WorkspaceCommandRunnerTests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void WorkspaceList_ReadsManifestMembers()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "A"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """
                {
                  "members": ["src/A"],
                  "index_strategy": "per_member",
                  "default_db_name": "index.db"
                }
                """);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = Path.Combine(root, "src", "A");
                var (exitCode, stdout, _) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["list", "--json"], _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Contains("cdidx.workspace.json", stdout);
                Assert.Contains("index.db", stdout);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsOversizedManifest()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_oversized");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            File.WriteAllText(manifestPath, new string('x', WorkspaceManifestLoader.MaxManifestBytes + 1));

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains($"{WorkspaceManifestLoader.MaxManifestBytes} byte limit", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsDeeplyNestedManifest()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_depth");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var nestedPrefix = string.Concat(Enumerable.Repeat("{\"nested\":", WorkspaceManifestLoader.MaxManifestDepth + 1));
            File.WriteAllText(manifestPath, nestedPrefix + "0" + new string('}', WorkspaceManifestLoader.MaxManifestDepth + 1));

            Assert.ThrowsAny<JsonException>(() => WorkspaceManifestLoader.Load(manifestPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsTooManyMembers()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_members");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var members = string.Join(",", Enumerable.Range(0, WorkspaceManifestLoader.MaxManifestMembers + 1).Select(i => $"\"src{i}\""));
            File.WriteAllText(manifestPath, $$"""
                {
                  "members": [{{members}}]
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains($"{WorkspaceManifestLoader.MaxManifestMembers} member limit", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsOverlongMemberPath()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_member_length");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var member = new string('a', WorkspaceManifestLoader.MaxManifestMemberPathChars + 1);
            File.WriteAllText(manifestPath, $$"""
                {
                  "members": [{{JsonSerializer.Serialize(member)}}]
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains($"{WorkspaceManifestLoader.MaxManifestMemberPathChars} character limit", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsInvalidMemberEntriesWithBoundedDiagnostics_Issue3429()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_invalid_members");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var longMember = new string('a', WorkspaceManifestLoader.MaxManifestMemberPathChars + 1);
            File.WriteAllText(manifestPath, $$"""
                {
                  "members": [
                    "src/A",
                    "",
                    42,
                    true,
                    "   ",
                    {{JsonSerializer.Serialize(longMember)}}
                  ]
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains("members contain invalid entries", ex.Message);
            Assert.Contains("members[1]", ex.Message);
            Assert.Contains("members[2]", ex.Message);
            Assert.Contains("members[3]", ex.Message);
            Assert.Contains("members[4]", ex.Message);
            Assert.Contains("members[5]", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_NormalizesAndDedupesMembers_Issue3429()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_dedupe");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            File.WriteAllText(manifestPath, """
                {
                  "members": [
                    "src/A",
                    "src/./A/",
                    "src/B/"
                  ]
                }
                """);

            var manifest = WorkspaceManifestLoader.Load(manifestPath);

            Assert.Equal(2, manifest.Members.Count);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "A")), manifest.Members[0].Path);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "B")), manifest.Members[1].Path);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsRootedMemberPath()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_rooted_member");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var absoluteMember = Path.Combine(root, "src", "A");
            File.WriteAllText(manifestPath, $$"""
                {
                  "members": [{{JsonSerializer.Serialize(absoluteMember)}}]
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains("member path must be relative", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsEscapingMemberPath()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_escaping_member");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            File.WriteAllText(manifestPath, """
                {
                  "members": ["../outside"]
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains("member path escapes the manifest root", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_UsesPathCasingForContainment_Issue3429()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_case");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var alternateRootName = SwapAsciiCase(Path.GetFileName(root));
            Assert.NotEqual(Path.GetFileName(root), alternateRootName);
            var member = Path.Combine("..", alternateRootName, "src");
            File.WriteAllText(manifestPath, $$"""
                {
                  "members": [{{JsonSerializer.Serialize(member)}}]
                }
                """);

            PathCasing.ResetCacheForTests();
            PathCasing.SeedFromWorkspace(root, ignoreCase: false);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains("member path escapes the manifest root", ex.Message);
        }
        finally
        {
            PathCasing.ResetCacheForTests();
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsOverlongDefaultDbName()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_db_name_length");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var dbName = new string('a', WorkspaceManifestLoader.MaxDefaultDbNameChars + 1);
            File.WriteAllText(manifestPath, $$"""
                {
                  "default_db_name": {{JsonSerializer.Serialize(dbName)}}
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains($"{WorkspaceManifestLoader.MaxDefaultDbNameChars} character limit", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsAbsoluteDefaultDbName()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_absolute_db");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var dbName = Path.Combine(root, "outside.db");
            File.WriteAllText(manifestPath, $$"""
                {
                  "default_db_name": {{JsonSerializer.Serialize(dbName)}}
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains("default_db_name must be a plain file name", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside.db")]
    [InlineData("nested/index.db")]
    public void WorkspaceManifestLoader_Load_RejectsUnsafeDefaultDbName(string dbName)
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_unsafe_db");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            File.WriteAllText(manifestPath, $$"""
                {
                  "default_db_name": {{JsonSerializer.Serialize(dbName)}}
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains("default_db_name must be a plain file name", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_AcceptsUtf8BomManifest()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_bom");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            var json = """
                {
                  "members": ["src/A"],
                  "index_strategy": "per_member",
                  "default_db_name": "index.db"
                }
                """;
            File.WriteAllBytes(manifestPath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]);

            var manifest = WorkspaceManifestLoader.Load(manifestPath);

            Assert.Equal("index.db", manifest.DefaultDbName);
            Assert.Single(manifest.Members);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Load_RejectsUnknownIndexStrategy()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_index_strategy");
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            File.WriteAllText(manifestPath, """
                {
                  "members": ["src/A"],
                  "index_strategy": "singel"
                }
                """);

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains("index_strategy must be 'per_member' or 'single'", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifestLoader_Find_RejectsInvalidStartDirectory_Issue3429()
    {
        var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Find("\0"));

        Assert.Contains("discovery start directory is invalid", ex.Message);
    }

    [Fact]
    public void WorkspaceErrors_HonorJsonFlag()
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["nope", "--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("\"status\":\"error\"", stdout);
        Assert.Contains("Unknown workspace command", stdout);
        Assert.DoesNotContain("Unknown workspace command", stderr);
    }

    [Fact]
    public void ConfigErrors_HonorJsonFlag()
    {
        var configHome = TestProjectHelper.CreateTempProject("cdidx_config_error_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["config", "nope", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("\"status\":\"error\"", stdout);
            Assert.Contains("Unknown config command", stdout);
            Assert.DoesNotContain("Unknown config command", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ConfigShowErrors_HonorJsonFlag()
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => CdidxConfigFile.RunShow(["extra", "--json"], _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("\"status\":\"error\"", stdout);
        Assert.Contains("config show does not accept positional arguments", stdout);
        Assert.DoesNotContain("config show does not accept positional arguments", stderr);
    }

    [Fact]
    public void ConfigShow_PrintsPrecedence()
    {
        var configHome = TestProjectHelper.CreateTempProject("cdidx_config_show_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var (exitCode, stdout, _) = ConsoleCapture.Capture(() => CdidxConfigFile.RunShow(["--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.False(root.TryGetProperty("active_workspace", out _));
            Assert.Contains(
                root.GetProperty("precedence").EnumerateArray(),
                item => item.GetString() == "active_workspace");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ConfigShowJson_IncludesMissingMetadataAndEffectiveDefaults_Issue3905()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_config_show_missing_metadata");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_config_show_missing_metadata_home");
        var previous = Environment.CurrentDirectory;
        try
        {
            using var env = EnvironmentVariableScope.Capture(
                ActiveWorkspace.EnvironmentVariable,
                "XDG_CONFIG_HOME",
                CdidxConfigFile.DisableEnvVar,
                QueryCommandRunner.DefaultLimitEnvironmentVariable,
                QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
                "CDIDX_GITHUB_TOKEN");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable(CdidxConfigFile.DisableEnvVar, null);
            Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, null);
            Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", null);
            Environment.CurrentDirectory = root;
            var currentRoot = Environment.CurrentDirectory;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => CdidxConfigFile.RunShow(["--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.False(payload.TryGetProperty("config_path", out _));
            Assert.Equal("not_found", payload.GetProperty("config_file").GetProperty("status").GetString());
            Assert.Equal("not_found", payload.GetProperty("config_file").GetProperty("reason").GetString());
            Assert.Contains(
                payload.GetProperty("config_file").GetProperty("supported_files").EnumerateArray(),
                item => item.GetString() == CdidxConfigFile.ProjectConfigRelativePath);
            Assert.Contains(
                payload.GetProperty("searched_paths").EnumerateArray(),
                item => item.GetString() == Path.Combine(currentRoot, CdidxConfigFile.ProjectConfigRelativePath));
            Assert.Equal("not_found", payload.GetProperty("active_workspace_status").GetProperty("status").GetString());
            Assert.Equal("not_found", payload.GetProperty("workspace_manifest").GetProperty("status").GetString());
            Assert.Contains(
                payload.GetProperty("workspace_manifest").GetProperty("searched_paths").EnumerateArray(),
                item => item.GetString() == Path.Combine(currentRoot, WorkspaceManifestLoader.FileName));

            var defaultLimit = payload.GetProperty("effective_config").GetProperty(QueryCommandRunner.DefaultLimitEnvironmentVariable);
            Assert.Equal("default", defaultLimit.GetProperty("source").GetString());
            Assert.Equal("20", defaultLimit.GetProperty("value").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ConfigShowJson_IncludesSourcesAndRedactsSecrets_Issue3927()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_config_show_sources");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_config_show_sources_home");
        var previous = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(root, CdidxConfigFile.FileName), """
                {
                  "search": {
                    "limit": 37
                  }
                }
                """);
            using var env = EnvironmentVariableScope.Capture(
                ActiveWorkspace.EnvironmentVariable,
                "XDG_CONFIG_HOME",
                CdidxConfigFile.DisableEnvVar,
                QueryCommandRunner.DefaultLimitEnvironmentVariable,
                QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable,
                "CDIDX_GITHUB_TOKEN");
            const string secret = "ghp_123456789012345678901234567890123456";
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable(CdidxConfigFile.DisableEnvVar, null);
            Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultLimitEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable, "5");
            Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", secret);
            Environment.CurrentDirectory = root;
            var currentRoot = Environment.CurrentDirectory;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["config", "show", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            Assert.DoesNotContain(secret, stdout);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.Equal("loaded", payload.GetProperty("config_file").GetProperty("status").GetString());
            Assert.Equal(Path.Combine(currentRoot, CdidxConfigFile.FileName), payload.GetProperty("config_file").GetProperty("path").GetString());

            var effective = payload.GetProperty("effective_config");
            var limit = effective.GetProperty(QueryCommandRunner.DefaultLimitEnvironmentVariable);
            Assert.Equal("config_file", limit.GetProperty("source").GetString());
            Assert.Equal("37", limit.GetProperty("value").GetString());
            var snippetLines = effective.GetProperty(QueryCommandRunner.DefaultSnippetLinesEnvironmentVariable);
            Assert.Equal("environment", snippetLines.GetProperty("source").GetString());
            Assert.Equal("5", snippetLines.GetProperty("value").GetString());
            var githubToken = effective.GetProperty("CDIDX_GITHUB_TOKEN");
            Assert.Equal("environment", githubToken.GetProperty("source").GetString());
            Assert.True(githubToken.GetProperty("sensitive").GetBoolean());
            Assert.Equal("<redacted>", githubToken.GetProperty("value").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ConfigShowJson_InvalidConfigReportsInvalidStatus_Issue3927()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_config_show_invalid");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_config_show_invalid_home");
        var previous = Environment.CurrentDirectory;
        try
        {
            File.WriteAllText(Path.Combine(root, CdidxConfigFile.FileName), "{ invalid json");
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME", CdidxConfigFile.DisableEnvVar);
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.SetEnvironmentVariable(CdidxConfigFile.DisableEnvVar, null);
            Environment.CurrentDirectory = root;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["config", "show", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var configFile = document.RootElement.GetProperty("config_file");
            Assert.Equal("invalid", configFile.GetProperty("status").GetString());
            Assert.Contains("Invalid JSON", configFile.GetProperty("error").GetString(), StringComparison.Ordinal);

            var (prettyExitCode, prettyStdout, prettyStderr) = ConsoleCapture.Capture(() => ProgramRunner.Run(["--pretty", "config", "show", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, prettyExitCode);
            Assert.Empty(prettyStderr);
            using var prettyDocument = JsonDocument.Parse(prettyStdout);
            Assert.Equal("invalid", prettyDocument.RootElement.GetProperty("config_file").GetProperty("status").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ConfigShowJsonUnsupportedMode_ReturnsStructuredJsonError_Issue3927()
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => CdidxConfigFile.RunShow(["--json=array"], _jsonOptions));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var payload = document.RootElement;
        Assert.Equal("error", payload.GetProperty("status").GetString());
        Assert.Contains("--json=<format>", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceStatusJsonNoManifest_IncludesDiscoveryMetadata_Issue3905()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_status_no_manifest");
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var currentRoot = Environment.CurrentDirectory;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var payload = document.RootElement;
            Assert.False(payload.TryGetProperty("manifest", out _));
            Assert.Empty(payload.GetProperty("members").EnumerateArray());
            var manifestStatus = payload.GetProperty("manifest_status");
            Assert.Equal("not_found", manifestStatus.GetProperty("status").GetString());
            Assert.Equal("not_found", manifestStatus.GetProperty("reason").GetString());
            Assert.Contains(
                manifestStatus.GetProperty("supported_files").EnumerateArray(),
                item => item.GetString() == WorkspaceManifestLoader.FileName);
            Assert.Contains(
                manifestStatus.GetProperty("searched_paths").EnumerateArray(),
                item => item.GetString() == Path.Combine(currentRoot, WorkspaceManifestLoader.FileName));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ActiveWorkspace_AffectsQueryResolutionButNotIndexResolution()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_project");
        var activeRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_state");
        var activeDb = Path.Combine(activeRoot, ".cdidx", "codeindex.db");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable);
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, activeDb);

            var query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
            var index = DbPathResolver.ResolveForIndex(projectRoot, explicitDbPath: null, explicitDataDir: null);

            Assert.Equal(Path.GetFullPath(activeDb), query.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceActiveWorkspace, query.DataDirSource);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), index.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, index.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(activeRoot);
        }
    }

    [Fact]
    public void ActiveWorkspaceEnvironment_RelativeDbPath_DoesNotOverrideQueryResolution_Issue3825()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_env_relative_project");
        const string RelativeDbPath = "relative_active_workspace_SENTINEL_3825.db";
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable);
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, RelativeDbPath);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains(ActiveWorkspace.EnvironmentVariable, stderr);
            Assert.Contains("value must be an absolute database path", stderr);
            Assert.DoesNotContain(RelativeDbPath, stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void MalformedActiveWorkspaceState_DoesNotOverrideQueryResolution()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_malformed_project");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_malformed_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
            File.WriteAllText(ActiveWorkspace.StatePath, "{");

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("Ignoring active workspace state", stderr);
            Assert.DoesNotContain(configHome, stderr);
            Assert.DoesNotContain(ActiveWorkspace.StatePath, stderr);
            Assert.DoesNotContain("LineNumber", stderr);
            Assert.Contains("invalid JSON", stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ActiveWorkspaceState_ControlCharacterName_DoesNotOverrideQueryResolution_Issue3825()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_bad_name_project");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_bad_name_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
            File.WriteAllText(ActiveWorkspace.StatePath, $$"""
                {
                  "name": "bad\nname",
                  "root": {{JsonSerializer.Serialize(projectRoot)}},
                  "db_path": {{JsonSerializer.Serialize(Path.Combine(projectRoot, ".cdidx", "codeindex.db"))}}
                }
                """);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("Ignoring active workspace state", stderr);
            Assert.Contains("`name` must not contain control characters", stderr);
            Assert.DoesNotContain("bad", stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ActiveWorkspaceState_OversizedName_DoesNotOverrideQueryResolution_Issue3825()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_long_name_project");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_long_name_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
            File.WriteAllText(ActiveWorkspace.StatePath, $$"""
                {
                  "name": {{JsonSerializer.Serialize(new string('n', ActiveWorkspace.MaxWorkspaceNameChars + 1))}},
                  "root": {{JsonSerializer.Serialize(projectRoot)}},
                  "db_path": {{JsonSerializer.Serialize(Path.Combine(projectRoot, ".cdidx", "codeindex.db"))}}
                }
                """);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("Ignoring active workspace state", stderr);
            Assert.Contains($"`name` exceeds {ActiveWorkspace.MaxWorkspaceNameChars} characters", stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ActiveWorkspaceState_MissingRoot_DoesNotOverrideQueryResolution_Issue3430()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_missing_root_project");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_missing_root_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
            File.WriteAllText(ActiveWorkspace.StatePath, $$"""
                {
                  "name": "active",
                  "db_path": {{JsonSerializer.Serialize(Path.Combine(projectRoot, ".cdidx", "codeindex.db"))}}
                }
                """);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("Ignoring active workspace state", stderr);
            Assert.Contains("`root` is required", stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ActiveWorkspaceState_MissingDbPath_DoesNotOverrideQueryResolution_Issue3430()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_missing_db_project");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_missing_db_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
            File.WriteAllText(ActiveWorkspace.StatePath, $$"""
                {
                  "name": "active",
                  "root": {{JsonSerializer.Serialize(projectRoot)}}
                }
                """);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("Ignoring active workspace state", stderr);
            Assert.Contains("`db_path` is required", stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ActiveWorkspaceState_DbPathOutsideRoot_DoesNotOverrideQueryResolution_Issue3430()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_outside_db_project");
        var outsideRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_outside_db");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_outside_db_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
            File.WriteAllText(ActiveWorkspace.StatePath, $$"""
                {
                  "name": "active",
                  "root": {{JsonSerializer.Serialize(projectRoot)}},
                  "db_path": {{JsonSerializer.Serialize(Path.Combine(outsideRoot, ".cdidx", "codeindex.db"))}}
                }
                """);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("Ignoring active workspace state", stderr);
            Assert.Contains("`db_path` must be inside `root`", stderr);
            Assert.DoesNotContain(outsideRoot, stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(outsideRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void ActiveWorkspaceState_RelativeConfigHome_DoesNotOverrideQueryResolution_Issue3430()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_relative_config_project");
        const string RelativeConfigHome = "relative_config_HOME_SENTINEL_3430";
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", RelativeConfigHome);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("Ignoring active workspace config home", stderr);
            Assert.Contains("XDG_CONFIG_HOME must be an absolute path", stderr);
            Assert.DoesNotContain(RelativeConfigHome, stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void DeeplyNestedActiveWorkspaceState_DoesNotOverrideQueryResolution_Issue3036()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_depth_project");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_depth_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
            var activeDbPath = Path.Combine(configHome, "active.db");
            var nestedPrefix = string.Concat(Enumerable.Repeat("""{"next":""", ActiveWorkspace.MaxStateJsonDepth + 1));
            var nested = nestedPrefix + "0" + new string('}', ActiveWorkspace.MaxStateJsonDepth + 1);
            File.WriteAllText(ActiveWorkspace.StatePath, $$"""
                {
                  "name": "active",
                  "root": {{JsonSerializer.Serialize(configHome)}},
                  "db_path": {{JsonSerializer.Serialize(activeDbPath)}},
                  "extra": {{nested}}
                }
                """);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("Ignoring active workspace state", stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void OversizedActiveWorkspaceEnvironment_DoesNotOverrideQueryResolution_Issue3164()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_env_large_project");
        try
        {
            var raw = new string('a', ActiveWorkspace.MaxEnvironmentPathChars) + "TAIL_ISSUE_3164";
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable);
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, raw);

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains(ActiveWorkspace.EnvironmentVariable, stderr);
            Assert.Contains($"value exceeds {ActiveWorkspace.MaxEnvironmentPathChars} characters", stderr);
            Assert.DoesNotContain("TAIL_ISSUE_3164", stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ActiveWorkspaceSave_OnPosix_WritesPrivateStateFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_private_config");
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_private_project");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            ActiveWorkspace.Save(new ActiveWorkspaceState("default", projectRoot, Path.Combine(projectRoot, ".cdidx", "codeindex.db")));

            Assert.Equal(
                DataDirectorySecurity.PrivateDirectoryMode,
                File.GetUnixFileMode(Path.GetDirectoryName(ActiveWorkspace.StatePath)!) & DataDirectorySecurity.PermissionBits);
            Assert.Equal(
                DataDirectorySecurity.PrivateFileMode,
                File.GetUnixFileMode(ActiveWorkspace.StatePath) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void OversizedActiveWorkspaceState_DoesNotOverrideQueryResolution()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_active_workspace_large_project");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_active_workspace_large_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Directory.CreateDirectory(Path.GetDirectoryName(ActiveWorkspace.StatePath)!);
            File.WriteAllText(ActiveWorkspace.StatePath, new string('x', 65 * 1024));

            DbPathResolution? query = null;
            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                query = DbPathResolver.ResolveForQuery(projectRoot, explicitDbPath: null, explicitDataDir: null);
                return 0;
            });

            Assert.NotNull(query);
            Assert.Contains("file exceeds", stderr);
            Assert.Equal(Path.Combine(projectRoot, ".cdidx", "codeindex.db"), query!.DbPath);
            Assert.Equal(DbPathResolver.DataDirSourceWorkspace, query.DataDirSource);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceUse_RejectsUnknownManifestMember()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_use_unknown");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_use_config");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "A"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/A"] }""");
            using var env = EnvironmentVariableScope.Capture("XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "typo"], _jsonOptions));

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Contains("workspace member was not found", stderr);
                Assert.False(File.Exists(ActiveWorkspace.StatePath));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceUse_RejectsMissingManifestMember()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_use_missing");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_use_missing_config");
        try
        {
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/Missing"] }""");
            using var env = EnvironmentVariableScope.Capture("XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "Missing"], _jsonOptions));

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Contains("workspace member is missing on disk", stderr);
                Assert.False(File.Exists(ActiveWorkspace.StatePath));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceUse_RejectsAmbiguousSameBasenameMembers()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_use_ambiguous");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_use_ambiguous_config");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "App"));
            Directory.CreateDirectory(Path.Combine(root, "tests", "App"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/App", "tests/App"] }""");
            using var env = EnvironmentVariableScope.Capture("XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "App"], _jsonOptions));

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Contains("workspace member name is ambiguous", stderr);
                Assert.Contains(Path.Combine("src", "App"), stderr);
                Assert.Contains(Path.Combine("tests", "App"), stderr);
                Assert.False(File.Exists(ActiveWorkspace.StatePath));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceManifest_EscapingMemberDiagnosticDoesNotLeakMemberPath_Issue3805()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_escape");
        const string SentinelMember = "../SECRET_MEMBER_SENTINEL_3805";
        try
        {
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            File.WriteAllText(manifestPath, $$"""{ "members": [{{JsonSerializer.Serialize(SentinelMember)}}] }""");

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains("member path escapes the manifest root", ex.Message);
            Assert.DoesNotContain(SentinelMember, ex.Message);
            Assert.DoesNotContain("SECRET_MEMBER_SENTINEL_3805", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceManifest_LongMemberPathDiagnosticDoesNotLeakMemberValue_Issue3805()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_manifest_long_member");
        try
        {
            var longMember = new string('a', WorkspaceManifestLoader.MaxManifestMemberPathChars + 1) + "TAIL_SENTINEL_3805";
            var manifestPath = Path.Combine(root, "cdidx.workspace.json");
            File.WriteAllText(manifestPath, $$"""{ "members": [{{JsonSerializer.Serialize(longMember)}}] }""");

            var ex = Assert.Throws<InvalidDataException>(() => WorkspaceManifestLoader.Load(manifestPath));

            Assert.Contains($"exceeds the {WorkspaceManifestLoader.MaxManifestMemberPathChars} character limit", ex.Message);
            Assert.DoesNotContain("TAIL_SENTINEL_3805", ex.Message);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceUse_CaseSensitiveMemberSelectionDistinguishesCaseOnlyBasenames_Issue3805()
    {
        PathCasing.ResetCacheForTests();
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_use_case_sensitive");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_use_case_sensitive_config");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "App"));
            Directory.CreateDirectory(Path.Combine(root, "src", "app"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/App", "src/app"] }""");
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            PathCasing.SeedFromWorkspace(root, ignoreCase: false);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "App"], _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                var state = ActiveWorkspace.Load();
                Assert.NotNull(state);
                Assert.Equal("App", state.Name);
                Assert.Equal(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "App")), state.Root);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            PathCasing.ResetCacheForTests();
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceUse_RejectsNamedWorkspaceWithoutManifest()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_use_no_manifest");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_use_no_manifest_config");
        try
        {
            using var env = EnvironmentVariableScope.Capture("XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "typo"], _jsonOptions));

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Contains("workspace manifest was not found", stderr);
                Assert.False(File.Exists(ActiveWorkspace.StatePath));
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceUse_RelativeConfigHomeReturnsSafeError_Issue3430()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_use_relative_config");
        const string RelativeConfigHome = "relative_config_HOME_SENTINEL_USE_3430";
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", RelativeConfigHome);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "default"], _jsonOptions));

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Contains("XDG_CONFIG_HOME must be an absolute path", stderr);
                Assert.DoesNotContain(RelativeConfigHome, stderr);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WorkspaceUse_SingleStrategyStoresManifestRoot_Issue3430()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_use_single_strategy");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_use_single_strategy_config");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "A"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """
                {
                  "members": ["src/A"],
                  "index_strategy": "single"
                }
                """);
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "A"], _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                var state = ActiveWorkspace.Load();
                Assert.NotNull(state);
                var expectedRoot = Path.GetFullPath(Environment.CurrentDirectory);
                Assert.Equal(expectedRoot, state.Root);
                Assert.Equal(Path.GetFullPath(Path.Combine(expectedRoot, ".cdidx", "codeindex.db")), state.DbPath);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceUseDefault_DoesNotSelectFirstManifestMember()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_use_default");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_use_default_config");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "A"));
            Directory.CreateDirectory(Path.Combine(root, "src", "B"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["src/A", "src/B"] }""");
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);

            var previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = root;
                var (exitCode, _, _) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "default"], _jsonOptions));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                var state = ActiveWorkspace.Load();
                Assert.NotNull(state);
                var expectedRoot = Path.GetFullPath(Environment.CurrentDirectory);
                Assert.Equal(expectedRoot, state.Root);
                Assert.Equal(Path.GetFullPath(Path.Combine(expectedRoot, ".cdidx", "codeindex.db")), state.DbPath);
            }
            finally
            {
                Environment.CurrentDirectory = previous;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    private static string SwapAsciiCase(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (ch is >= 'a' and <= 'z')
                chars[i] = (char)(ch - ('a' - 'A'));
            else if (ch is >= 'A' and <= 'Z')
                chars[i] = (char)(ch + ('a' - 'A'));
        }

        return new string(chars);
    }
}
