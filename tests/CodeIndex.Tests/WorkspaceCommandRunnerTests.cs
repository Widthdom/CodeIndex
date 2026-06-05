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
}
