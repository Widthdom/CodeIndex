using CodeIndex.Cli;
using System.Text.Json;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class WorkspaceCommandRunnerStatusActiveTests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void WorkspaceStatusJson_TracksInactiveActiveMissingAndStaleStates_Issue4358()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_config");
        var previous = Environment.CurrentDirectory;
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            env.Set(ActiveWorkspace.EnvironmentVariable, null);
            env.Set("XDG_CONFIG_HOME", configHome);
            Environment.CurrentDirectory = root;

            var inactive = RunStatus();
            Assert.False(inactive.GetProperty("active").GetBoolean());
            Assert.Equal(JsonValueKind.Null, inactive.GetProperty("workspace").ValueKind);
            Assert.Equal("inactive", inactive.GetProperty("status").GetString());
            Assert.Equal("not_set", inactive.GetProperty("reason").GetString());
            Assert.Equal("active_workspace_not_set", inactive.GetProperty("code").GetString());
            Assert.False(inactive.TryGetProperty("active_workspace", out _));

            Directory.CreateDirectory(Path.Combine(root, "member-a"));
            Directory.CreateDirectory(Path.Combine(root, "member-b"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["member-a", "member-b"] }""");
            var (useExitCode, _, useStderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "member-b", "--json"], _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, useExitCode);
            Assert.Empty(useStderr);

            var active = RunStatus();
            Assert.True(active.GetProperty("active").GetBoolean());
            Assert.Equal("active", active.GetProperty("status").GetString());
            Assert.Equal("active_workspace_set", active.GetProperty("code").GetString());
            Assert.Equal("member-b", active.GetProperty("workspace").GetProperty("name").GetString());
            Assert.Equal("member-b", active.GetProperty("active_workspace").GetProperty("name").GetString());

            Directory.Delete(Path.Combine(root, "member-b"));
            var missing = RunStatus();
            Assert.Equal("missing", missing.GetProperty("status").GetString());
            Assert.Equal("manifest_member_missing", missing.GetProperty("reason").GetString());
            Assert.Equal("active_workspace_member_missing", missing.GetProperty("code").GetString());

            Directory.CreateDirectory(Path.Combine(root, "member-b"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["member-a"] }""");
            var stale = RunStatus();
            Assert.Equal("stale", stale.GetProperty("status").GetString());
            Assert.Equal("manifest_member_not_found", stale.GetProperty("reason").GetString());
            Assert.Equal("active_workspace_member_not_found", stale.GetProperty("code").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    private JsonElement RunStatus()
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.GetProperty("active_workspace_status").Clone();
    }
}
