using CodeIndex.Cli;
using System.Text.Json;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class WorkspaceCommandRunnerStatusActiveTests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void WorkspaceStatusJsonNoActiveWorkspace_IncludesInactiveState_Issue4358()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_no_active");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_no_active_config");
        var previous = Environment.CurrentDirectory;
        try
        {
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.CurrentDirectory = root;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var activeStatus = document.RootElement.GetProperty("active_workspace_status");
            Assert.False(activeStatus.GetProperty("active").GetBoolean());
            Assert.Equal(JsonValueKind.Null, activeStatus.GetProperty("workspace").ValueKind);
            Assert.Equal("inactive", activeStatus.GetProperty("status").GetString());
            Assert.Equal("not_set", activeStatus.GetProperty("reason").GetString());
            Assert.Equal("active_workspace_not_set", activeStatus.GetProperty("code").GetString());
            Assert.False(activeStatus.TryGetProperty("active_workspace", out _));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceStatusJsonActiveWorkspace_IncludesCurrentStatus_Issue4358()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_active");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_active_config");
        var previous = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "member-a"));
            Directory.CreateDirectory(Path.Combine(root, "member-b"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["member-a", "member-b"] }""");
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.CurrentDirectory = root;

            var (useExitCode, _, useStderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "member-b", "--json"], _jsonOptions));
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, useExitCode);
            Assert.Empty(useStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var activeStatus = document.RootElement.GetProperty("active_workspace_status");
            Assert.True(activeStatus.GetProperty("active").GetBoolean());
            Assert.Equal("active", activeStatus.GetProperty("status").GetString());
            Assert.Equal("active_workspace_set", activeStatus.GetProperty("code").GetString());
            Assert.Equal("member-b", activeStatus.GetProperty("workspace").GetProperty("name").GetString());
            Assert.Equal("member-b", activeStatus.GetProperty("active_workspace").GetProperty("name").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceStatusJsonActiveWorkspace_ReportsMissingMember_Issue4358()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_missing_active");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_missing_active_config");
        var previous = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "member-a"));
            Directory.CreateDirectory(Path.Combine(root, "member-b"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["member-a", "member-b"] }""");
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.CurrentDirectory = root;

            var (useExitCode, _, useStderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "member-b", "--json"], _jsonOptions));
            Directory.Delete(Path.Combine(root, "member-b"));
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, useExitCode);
            Assert.Empty(useStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var activeStatus = document.RootElement.GetProperty("active_workspace_status");
            Assert.True(activeStatus.GetProperty("active").GetBoolean());
            Assert.Equal("missing", activeStatus.GetProperty("status").GetString());
            Assert.Equal("manifest_member_missing", activeStatus.GetProperty("reason").GetString());
            Assert.Equal("active_workspace_member_missing", activeStatus.GetProperty("code").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }

    [Fact]
    public void WorkspaceStatusJsonActiveWorkspace_ReportsStaleMember_Issue4358()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_stale_active");
        var configHome = TestProjectHelper.CreateTempProject("cdidx_workspace_status_4358_stale_active_config");
        var previous = Environment.CurrentDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "member-a"));
            Directory.CreateDirectory(Path.Combine(root, "member-b"));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["member-a", "member-b"] }""");
            using var env = EnvironmentVariableScope.Capture(ActiveWorkspace.EnvironmentVariable, "XDG_CONFIG_HOME");
            Environment.SetEnvironmentVariable(ActiveWorkspace.EnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            Environment.CurrentDirectory = root;

            var (useExitCode, _, useStderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["use", "member-b", "--json"], _jsonOptions));
            File.WriteAllText(Path.Combine(root, "cdidx.workspace.json"), """{ "members": ["member-a"] }""");
            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() => WorkspaceCommandRunner.Run(["status", "--json"], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, useExitCode);
            Assert.Empty(useStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var activeStatus = document.RootElement.GetProperty("active_workspace_status");
            Assert.True(activeStatus.GetProperty("active").GetBoolean());
            Assert.Equal("stale", activeStatus.GetProperty("status").GetString());
            Assert.Equal("manifest_member_not_found", activeStatus.GetProperty("reason").GetString());
            Assert.Equal("active_workspace_member_not_found", activeStatus.GetProperty("code").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TestProjectHelper.DeleteDirectory(root);
            TestProjectHelper.DeleteDirectory(configHome);
        }
    }
}
