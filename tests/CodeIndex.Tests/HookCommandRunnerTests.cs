using System.Text.Json;
using CodeIndex.Cli;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class HookCommandRunnerTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Hooks_InstallStatusUninstall_ManagesPreCommitHook()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_install");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);

            var installExit = RunHooksAndCaptureStreams(["install", "--project", projectRoot]).ExitCode;
            var hooksDir = Path.Combine(projectRoot, ".git", "hooks");
            var hookPath = Path.Combine(hooksDir, "pre-commit");

            Assert.Equal(CommandExitCodes.Success, installExit);
            Assert.True(File.Exists(hookPath));
            var hook = File.ReadAllText(hookPath);
            Assert.Contains("BEGIN CDIDX MANAGED PRE-COMMIT", hook);
            Assert.Contains("cdidx index . --quiet", hook);

            var statusExit = RunHooksAndCaptureStreams(["status", "--project", projectRoot]).ExitCode;
            Assert.Equal(CommandExitCodes.Success, statusExit);

            var uninstallExit = RunHooksAndCaptureStreams(["uninstall", "--project", projectRoot]).ExitCode;
            Assert.Equal(CommandExitCodes.Success, uninstallExit);
            Assert.False(File.Exists(hookPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_StatusJson_UsesSourceGeneratedSerializer()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_status_json");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);

            var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(["status", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("absent", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(projectRoot, document.RootElement.GetProperty("project_path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_Install_ChainsExistingPreCommitHook()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_chain");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hooksDir = Path.Combine(projectRoot, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            var hookPath = Path.Combine(hooksDir, "pre-commit");
            var chainedHookPath = Path.Combine(hooksDir, "pre-commit.cdidx-chain");
            File.WriteAllText(hookPath, "#!/bin/sh\necho existing\n");

            var exitCode = RunHooksAndCaptureStreams(["install", "--project", projectRoot]).ExitCode;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(File.Exists(hookPath));
            Assert.True(File.Exists(chainedHookPath));
            Assert.Contains("echo existing", File.ReadAllText(chainedHookPath));
            Assert.Contains(chainedHookPath, File.ReadAllText(hookPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_TreatsOversizedPreCommitHookAsCustom()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_oversized");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hooksDir = Path.Combine(projectRoot, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            var hookPath = Path.Combine(hooksDir, "pre-commit");
            var chainedHookPath = Path.Combine(hooksDir, "pre-commit.cdidx-chain");
            File.WriteAllText(hookPath, new string('x', HookCommandRunner.MaxHookMarkerBytes + 1));

            var (statusExit, statusStdout, _) = RunHooksAndCaptureStreams(["status", "--project", projectRoot, "--json"]);
            var uninstallExit = RunHooksAndCaptureStreams(["uninstall", "--project", projectRoot]).ExitCode;
            var installExit = RunHooksAndCaptureStreams(["install", "--project", projectRoot]).ExitCode;

            Assert.Equal(CommandExitCodes.Success, statusExit);
            using (var document = JsonDocument.Parse(statusStdout))
                Assert.Equal("custom", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(CommandExitCodes.UsageError, uninstallExit);
            Assert.Equal(CommandExitCodes.Success, installExit);
            Assert.True(File.Exists(chainedHookPath));
            Assert.Equal(HookCommandRunner.MaxHookMarkerBytes + 1, File.ReadAllText(chainedHookPath).Length);
            Assert.Contains("BEGIN CDIDX MANAGED PRE-COMMIT", File.ReadAllText(hookPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Index_QuietSuppressesSuccessfulHumanOutput()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("quiet_index");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "class Program { static void Main() {} }\n");

            var (exitCode, stdout, stderr) = RunIndexAndCaptureStreams([projectRoot, "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Index_QuietStillPurgesStaleFiles()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("quiet_purge");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "Program.cs");
            File.WriteAllText(sourcePath, "class Program { static void Main() {} }\n");
            Assert.Equal(CommandExitCodes.Success, RunIndexAndCaptureStreams([projectRoot]).ExitCode);

            File.Delete(sourcePath);
            var (exitCode, stdout, stderr) = RunIndexAndCaptureStreams([projectRoot, "--quiet"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(0, CountIndexedPath(projectRoot, "Program.cs"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunIndexAndCaptureStreams(string[] args)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = IndexCommandRunner.Run(args, _jsonOptions);
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    private (int ExitCode, string StdOut, string StdErr) RunHooksAndCaptureStreams(string[] args)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = HookCommandRunner.Run(args, _jsonOptions);
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    private static long CountIndexedPath(string projectRoot, string relativePath)
    {
        var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files WHERE path = $path";
        command.Parameters.AddWithValue("$path", relativePath);
        return (long)command.ExecuteScalar()!;
    }
}
