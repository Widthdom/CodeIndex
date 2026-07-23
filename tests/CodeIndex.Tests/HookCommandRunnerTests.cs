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
            Assert.Contains($"cdidx index {QuoteShellForTest(projectRoot)} --quiet", hook);

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
    public void Hooks_InstallJson_DistinguishesInstalledUpdatedAndAlreadyInstalled_Issue4716()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_install_statuses");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hookPath = Path.Combine(projectRoot, ".git", "hooks", "pre-commit");

            var firstInstall = RunHooksAndCaptureStreams(["install", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, firstInstall.ExitCode);
            Assert.Equal(string.Empty, firstInstall.StdErr);
            using (var document = JsonDocument.Parse(firstInstall.StdOut))
                Assert.Equal("installed", document.RootElement.GetProperty("status").GetString());

            File.SetLastWriteTimeUtc(hookPath, new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc));
            var installedWriteTime = File.GetLastWriteTimeUtc(hookPath);

            var repeatedInstall = RunHooksAndCaptureStreams(["install", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, repeatedInstall.ExitCode);
            Assert.Equal(string.Empty, repeatedInstall.StdErr);
            using (var document = JsonDocument.Parse(repeatedInstall.StdOut))
                Assert.Equal("already_installed", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(installedWriteTime, File.GetLastWriteTimeUtc(hookPath));

            File.WriteAllText(
                hookPath,
                "#!/bin/sh\n# BEGIN CDIDX MANAGED PRE-COMMIT\necho stale\n# END CDIDX MANAGED PRE-COMMIT\n");

            var updatedInstall = RunHooksAndCaptureStreams(["install", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, updatedInstall.ExitCode);
            Assert.Equal(string.Empty, updatedInstall.StdErr);
            using (var document = JsonDocument.Parse(updatedInstall.StdOut))
                Assert.Equal("updated", document.RootElement.GetProperty("status").GetString());
            Assert.Contains($"cdidx index {QuoteShellForTest(projectRoot)} --quiet", File.ReadAllText(hookPath));
            Assert.DoesNotContain("echo stale", File.ReadAllText(hookPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_Install_RepairsNonExecutableManagedHook_Issue4716()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("hook_install_mode");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hookPath = Path.Combine(projectRoot, ".git", "hooks", "pre-commit");
            Assert.Equal(
                CommandExitCodes.Success,
                RunHooksAndCaptureStreams(["install", "--project", projectRoot, "--json"]).ExitCode);

            File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var preview = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, preview.ExitCode);
            using (var document = JsonDocument.Parse(preview.StdOut))
            {
                Assert.Equal("updated", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("replace_managed", document.RootElement.GetProperty("planned_action").GetString());
            }
            Assert.Equal(
                UnixFileMode.None,
                File.GetUnixFileMode(hookPath)
                    & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));

            var repair = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, repair.ExitCode);
            using (var document = JsonDocument.Parse(repair.StdOut))
                Assert.Equal("updated", document.RootElement.GetProperty("status").GetString());
            Assert.NotEqual(
                UnixFileMode.None,
                File.GetUnixFileMode(hookPath) & UnixFileMode.UserExecute);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_InstallDryRun_PreviewsCreateChainReplaceAndNoOpWithoutWriting_Issue4716()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_install_dry_run");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hooksDir = Path.Combine(projectRoot, ".git", "hooks");
            var hookPath = Path.Combine(hooksDir, "pre-commit");
            var chainedHookPath = Path.Combine(hooksDir, "pre-commit.cdidx-chain");
            TestProjectHelper.DeleteDirectory(hooksDir);

            var humanPreview = RunHooksAndCaptureStreams(["install", "--project", projectRoot, "--dry-run"]);

            Assert.Equal(CommandExitCodes.Success, humanPreview.ExitCode);
            Assert.Equal(string.Empty, humanPreview.StdErr);
            Assert.Contains("would be installed", humanPreview.StdOut, StringComparison.Ordinal);
            Assert.Contains("Planned action: create", humanPreview.StdOut, StringComparison.Ordinal);
            Assert.Contains("Managed hook preview:", humanPreview.StdOut, StringComparison.Ordinal);
            Assert.Contains("BEGIN CDIDX MANAGED PRE-COMMIT", humanPreview.StdOut, StringComparison.Ordinal);
            Assert.False(Directory.Exists(hooksDir));

            var createPreview = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, createPreview.ExitCode);
            Assert.Equal(string.Empty, createPreview.StdErr);
            string managedHookPreview;
            using (var document = JsonDocument.Parse(createPreview.StdOut))
            {
                Assert.Equal("installed", document.RootElement.GetProperty("status").GetString());
                Assert.True(document.RootElement.GetProperty("dry_run").GetBoolean());
                Assert.Equal("create", document.RootElement.GetProperty("planned_action").GetString());
                managedHookPreview = document.RootElement.GetProperty("managed_hook_preview").GetString()!;
                Assert.Contains("BEGIN CDIDX MANAGED PRE-COMMIT", managedHookPreview, StringComparison.Ordinal);
            }
            Assert.False(Directory.Exists(hooksDir));

            Directory.CreateDirectory(hooksDir);
            const string orphanChain = "#!/bin/sh\necho orphan chain\n";
            File.WriteAllText(chainedHookPath, orphanChain);

            var orphanChainPreview = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, orphanChainPreview.ExitCode);
            using (var document = JsonDocument.Parse(orphanChainPreview.StdOut))
            {
                Assert.Equal("installed", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("create", document.RootElement.GetProperty("planned_action").GetString());
                Assert.Equal(chainedHookPath, document.RootElement.GetProperty("chained_hook_path").GetString());
            }
            Assert.False(File.Exists(hookPath));
            Assert.Equal(orphanChain, File.ReadAllText(chainedHookPath));
            TestProjectHelper.DeleteFile(chainedHookPath);

            const string customHook = "#!/bin/sh\necho existing\n";
            File.WriteAllText(hookPath, customHook);

            var chainPreview = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, chainPreview.ExitCode);
            using (var document = JsonDocument.Parse(chainPreview.StdOut))
            {
                Assert.Equal("updated", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("chain_existing", document.RootElement.GetProperty("planned_action").GetString());
                Assert.Equal(chainedHookPath, document.RootElement.GetProperty("chained_hook_path").GetString());
            }
            Assert.Equal(customHook, File.ReadAllText(hookPath));
            Assert.False(File.Exists(chainedHookPath));

            const string existingChain = "#!/bin/sh\necho chained\n";
            File.WriteAllText(chainedHookPath, existingChain);

            var blockedPreview = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, blockedPreview.ExitCode);
            using (var document = JsonDocument.Parse(blockedPreview.StdOut))
            {
                Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
                Assert.True(document.RootElement.GetProperty("dry_run").GetBoolean());
                Assert.Equal("blocked", document.RootElement.GetProperty("planned_action").GetString());
                Assert.Contains(
                    "BEGIN CDIDX MANAGED PRE-COMMIT",
                    document.RootElement.GetProperty("managed_hook_preview").GetString(),
                    StringComparison.Ordinal);
            }
            Assert.Equal(customHook, File.ReadAllText(hookPath));
            Assert.Equal(existingChain, File.ReadAllText(chainedHookPath));

            var blockedHumanPreview = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--dry-run"]);

            Assert.Equal(CommandExitCodes.UsageError, blockedHumanPreview.ExitCode);
            Assert.Contains("chained hook already exists", blockedHumanPreview.StdErr, StringComparison.Ordinal);
            Assert.Contains("Planned action: blocked", blockedHumanPreview.StdOut, StringComparison.Ordinal);
            Assert.Contains("Managed hook preview:", blockedHumanPreview.StdOut, StringComparison.Ordinal);
            Assert.Contains("BEGIN CDIDX MANAGED PRE-COMMIT", blockedHumanPreview.StdOut, StringComparison.Ordinal);
            Assert.Equal(customHook, File.ReadAllText(hookPath));
            Assert.Equal(existingChain, File.ReadAllText(chainedHookPath));
            TestProjectHelper.DeleteFile(chainedHookPath);

            const string staleManagedHook = "#!/bin/sh\n# BEGIN CDIDX MANAGED PRE-COMMIT\necho stale\n# END CDIDX MANAGED PRE-COMMIT\n";
            File.WriteAllText(hookPath, staleManagedHook);

            var replacePreview = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, replacePreview.ExitCode);
            using (var document = JsonDocument.Parse(replacePreview.StdOut))
            {
                Assert.Equal("updated", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("replace_managed", document.RootElement.GetProperty("planned_action").GetString());
            }
            Assert.Equal(staleManagedHook, File.ReadAllText(hookPath));

            File.WriteAllText(hookPath, managedHookPreview);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    hookPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            var noOpPreview = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--dry-run", "--json"]);

            Assert.Equal(CommandExitCodes.Success, noOpPreview.ExitCode);
            using (var document = JsonDocument.Parse(noOpPreview.StdOut))
            {
                Assert.Equal("already_installed", document.RootElement.GetProperty("status").GetString());
                Assert.Equal("none", document.RootElement.GetProperty("planned_action").GetString());
            }
            Assert.Equal(managedHookPreview, File.ReadAllText(hookPath));
            Assert.False(File.Exists(chainedHookPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_DryRun_RejectsNonInstallSubcommand_Issue4716()
    {
        var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(["status", "--dry-run", "--json"]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "--dry-run is supported only for hooks install",
            document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Hooks_Install_QuotesSelectedProjectPathInGeneratedHook()
    {
        var parent = TestProjectHelper.CreateTempProject("hook_project_quote");
        var projectRoot = Path.Combine(parent, "repo with ' quote");
        try
        {
            Directory.CreateDirectory(projectRoot);
            TestProjectHelper.InitializeGitRepo(projectRoot);

            var exitCode = RunHooksAndCaptureStreams(["install", "--project", projectRoot]).ExitCode;
            var hookPath = Path.Combine(projectRoot, ".git", "hooks", "pre-commit");
            var hook = File.ReadAllText(hookPath);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains($"cdidx index {QuoteShellForTest(projectRoot)} --quiet", hook);
            Assert.DoesNotContain("cdidx index . --quiet", hook);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(parent);
        }
    }

    [Fact]
    public void Hooks_Install_RejectsSymlinkedGitDirectoryBeforeExternalWrite_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("hook_symlinked_git");
        var externalGitDir = TestProjectHelper.CreateTempProject("hook_external_git");
        var gitLink = Path.Combine(projectRoot, ".git");
        try
        {
            Directory.CreateSymbolicLink(gitLink, externalGitDir);

            var result = RunHooksAndCaptureStreams(["install", "--project", projectRoot]);

            Assert.Equal(CommandExitCodes.NotFound, result.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(externalGitDir, "hooks")));
            Assert.Contains("not a git repository", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteFile(gitLink);
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(externalGitDir);
        }
    }

    [Fact]
    public void Hooks_Install_RejectsSymlinkedHooksDescendantBeforeExternalWrite_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("hook_symlinked_hooks");
        var externalHooksDir = TestProjectHelper.CreateTempProject("hook_external_hooks");
        var gitDir = Path.Combine(projectRoot, ".git");
        var hooksLink = Path.Combine(gitDir, "hooks");
        try
        {
            Directory.CreateDirectory(gitDir);
            Directory.CreateSymbolicLink(hooksLink, externalHooksDir);

            var result = RunHooksAndCaptureStreams(["install", "--project", projectRoot]);

            Assert.Equal(CommandExitCodes.InstallError, result.ExitCode);
            Assert.False(File.Exists(Path.Combine(externalHooksDir, "pre-commit")));
            Assert.Contains("unsafe Git hooks metadata path", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteFile(hooksLink);
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(externalHooksDir);
        }
    }

    [Fact]
    public void Hooks_Install_RejectsSymlinkedHookFileBeforeExternalWrite_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("hook_symlinked_file");
        var externalHook = Path.Combine(TestProjectHelper.CreateTempProject("hook_external_file"), "pre-commit");
        var gitDir = Path.Combine(projectRoot, ".git");
        var hooksDir = Path.Combine(gitDir, "hooks");
        var hookLink = Path.Combine(hooksDir, "pre-commit");
        try
        {
            Directory.CreateDirectory(hooksDir);
            File.WriteAllText(externalHook, "external sentinel\n");
            File.CreateSymbolicLink(hookLink, externalHook);

            var result = RunHooksAndCaptureStreams(["install", "--project", projectRoot]);

            Assert.Equal(CommandExitCodes.InstallError, result.ExitCode);
            Assert.Equal("external sentinel\n", File.ReadAllText(externalHook));
            Assert.Contains("unsafe Git hook file path", result.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteFile(hookLink);
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(Path.GetDirectoryName(externalHook)!);
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
            Assert.Equal("pre-commit", document.RootElement.GetProperty("diagnostic_hook_path").GetString());
            var diagnosticProjectPath = document.RootElement.GetProperty("diagnostic_project_path").GetString();
            Assert.Contains("hook_status_json", diagnosticProjectPath, StringComparison.Ordinal);
            Assert.DoesNotContain(projectRoot, diagnosticProjectPath, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [ExternalProcessFact]
    public void Hooks_Uninstall_WithMissingHooksDirectory_RemainsIdempotent_Issue4599()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_uninstall_missing_hooks");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            TestProjectHelper.DeleteDirectory(Path.Combine(projectRoot, ".git", "hooks"));

            var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(
                ["uninstall", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("absent", document.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_UnknownOption_TruncatesOversizedToken()
    {
        var token = "--" + new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 20);

        var (exitCode, _, stderr) = RunHooksAndCaptureStreams([token]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("unknown option", stderr);
        Assert.Contains("<truncated; original length", stderr);
        Assert.DoesNotContain(token, stderr);
        Assert.DoesNotContain("Warning: unknown option", stderr);
    }

    [Fact]
    public void Hooks_UnknownOptionJson_WritesStructuredErrorWithoutStderr_Issue3683()
    {
        var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(["--json", "--bogus"]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("unknown option '--bogus'", document.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("Usage: cdidx hooks", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Hooks_CommandUnknownOption_ReturnsUsageError()
    {
        var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(["status", "--bogus"]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Usage: cdidx hooks", stderr);
        Assert.Contains("unknown option '--bogus'", stderr);
        Assert.DoesNotContain("Warning: unknown option", stderr);
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

            var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(
                ["install", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using (var document = JsonDocument.Parse(stdout))
                Assert.Equal("updated", document.RootElement.GetProperty("status").GetString());
            Assert.True(File.Exists(hookPath));
            Assert.True(File.Exists(chainedHookPath));
            Assert.Contains("echo existing", File.ReadAllText(chainedHookPath));
            Assert.Contains(chainedHookPath, File.ReadAllText(hookPath));
            AssertNoHookTempFiles(hooksDir);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_InstallForce_ReplacesExistingChainedHookAtomically()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_force_chain");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hooksDir = Path.Combine(projectRoot, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            var hookPath = Path.Combine(hooksDir, "pre-commit");
            var chainedHookPath = Path.Combine(hooksDir, "pre-commit.cdidx-chain");
            File.WriteAllText(hookPath, "#!/bin/sh\necho current\n");
            File.WriteAllText(chainedHookPath, "#!/bin/sh\necho stale\n");

            var exitCode = RunHooksAndCaptureStreams(["install", "--project", projectRoot, "--force"]).ExitCode;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("BEGIN CDIDX MANAGED PRE-COMMIT", File.ReadAllText(hookPath));
            Assert.Contains("echo current", File.ReadAllText(chainedHookPath));
            Assert.DoesNotContain("echo stale", File.ReadAllText(chainedHookPath));
            AssertNoHookTempFiles(hooksDir);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_InstallJson_ReportsStagedHookCleanupFailure()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_install_cleanup_warning");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hooksDir = Path.Combine(projectRoot, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            var hookPath = Path.Combine(hooksDir, "pre-commit");
            File.WriteAllText(hookPath, "#!/bin/sh\necho existing\n");
            HookCommandRunner.ReplaceFileForTesting = (_, _, _) => throw new IOException("replace denied");
            HookCommandRunner.DeleteFileForTesting = _ => throw new IOException("delete denied");

            var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(["install", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.InstallError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            var warnings = document.RootElement.GetProperty("warnings");
            Assert.Equal(2, warnings.GetArrayLength());
            var warning = warnings[0];
            Assert.Equal("staged_hook_temp", warning.GetProperty("category").GetString());
            Assert.Contains(".pre-commit.", warning.GetProperty("path").GetString(), StringComparison.Ordinal);
            Assert.Contains(".pre-commit.", warning.GetProperty("diagnostic_path").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain(projectRoot, warning.GetProperty("diagnostic_path").GetString(), StringComparison.Ordinal);
            Assert.Contains("failed to delete staged_hook_temp", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("IOException", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
            var backupWarning = warnings[1];
            Assert.Equal("chained_hook_backup", backupWarning.GetProperty("category").GetString());
            Assert.Contains("pre-commit.cdidx-chain", backupWarning.GetProperty("path").GetString(), StringComparison.Ordinal);
            Assert.Equal("pre-commit.cdidx-chain", backupWarning.GetProperty("diagnostic_path").GetString());
            Assert.Contains("failed to back up existing hook", backupWarning.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            HookCommandRunner.ReplaceFileForTesting = null;
            HookCommandRunner.DeleteFileForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_UninstallJson_ReportsManagedHookDeleteFailure()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_uninstall_cleanup_warning");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var installExit = RunHooksAndCaptureStreams(["install", "--project", projectRoot]).ExitCode;
            var hookPath = Path.Combine(projectRoot, ".git", "hooks", "pre-commit");
            Assert.Equal(CommandExitCodes.Success, installExit);
            Assert.True(File.Exists(hookPath));
            HookCommandRunner.DeleteFileForTesting = _ => throw new IOException("delete denied");

            var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(["uninstall", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.InstallError, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(File.Exists(hookPath));
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            var warning = document.RootElement.GetProperty("warnings")[0];
            Assert.Equal("managed_hook", warning.GetProperty("category").GetString());
            Assert.Equal(hookPath, warning.GetProperty("path").GetString());
            Assert.Equal("pre-commit", warning.GetProperty("diagnostic_path").GetString());
            Assert.Contains("failed to delete managed_hook", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("IOException", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            HookCommandRunner.DeleteFileForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_UninstallJson_ReportsChainedHookBackupFailure()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_uninstall_backup_warning");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hooksDir = Path.Combine(projectRoot, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            var hookPath = Path.Combine(hooksDir, "pre-commit");
            var chainedHookPath = Path.Combine(hooksDir, "pre-commit.cdidx-chain");
            File.WriteAllText(hookPath, "#!/bin/sh\necho existing\n");
            Assert.Equal(CommandExitCodes.Success, RunHooksAndCaptureStreams(["install", "--project", projectRoot]).ExitCode);
            Assert.True(File.Exists(chainedHookPath));
            HookCommandRunner.ReplaceFileForTesting = (_, _, _) => throw new IOException("restore denied");

            var (exitCode, stdout, stderr) = RunHooksAndCaptureStreams(["uninstall", "--project", projectRoot, "--json"]);

            Assert.Equal(CommandExitCodes.InstallError, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            var warning = document.RootElement.GetProperty("warnings")[0];
            Assert.Equal("chained_hook_backup", warning.GetProperty("category").GetString());
            Assert.Equal(chainedHookPath, warning.GetProperty("path").GetString());
            Assert.Equal("pre-commit.cdidx-chain", warning.GetProperty("diagnostic_path").GetString());
            Assert.Contains("failed to restore chained hook backup", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("IOException", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            HookCommandRunner.ReplaceFileForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Hooks_Uninstall_RestoresChainedPreCommitHook()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("hook_uninstall_chain");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            var hooksDir = Path.Combine(projectRoot, ".git", "hooks");
            Directory.CreateDirectory(hooksDir);
            var hookPath = Path.Combine(hooksDir, "pre-commit");
            var chainedHookPath = Path.Combine(hooksDir, "pre-commit.cdidx-chain");
            File.WriteAllText(hookPath, "#!/bin/sh\necho existing\n");

            Assert.Equal(CommandExitCodes.Success, RunHooksAndCaptureStreams(["install", "--project", projectRoot]).ExitCode);
            var uninstallExit = RunHooksAndCaptureStreams(["uninstall", "--project", projectRoot]).ExitCode;

            Assert.Equal(CommandExitCodes.Success, uninstallExit);
            Assert.True(File.Exists(hookPath));
            Assert.False(File.Exists(chainedHookPath));
            Assert.Contains("echo existing", File.ReadAllText(hookPath));
            Assert.DoesNotContain("BEGIN CDIDX MANAGED PRE-COMMIT", File.ReadAllText(hookPath));
            AssertNoHookTempFiles(hooksDir);
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
    public void Index_QuietFailureSuppressesBannerButKeepsError_Issue4340()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("quiet_failure");
        try
        {
            File.WriteAllText(Path.Combine(projectRoot, ".cdidx"), "not a directory\n");
            File.WriteAllText(Path.Combine(projectRoot, "Program.cs"), "class Program { static void Main() {} }\n");

            var (exitCode, stdout, stderr) = RunIndexAndCaptureStreams([projectRoot, "--quiet"]);

            Assert.NotEqual(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.DoesNotContain("Project :", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Output  :", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Mode    :", stderr, StringComparison.Ordinal);
            Assert.Contains("Error", stderr, StringComparison.Ordinal);
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

    [Fact]
    public void CreateStagedHookFileStream_OnPosix_CreatesPrivateFileUpFront_Issue3984()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("hook_private_staged");
        try
        {
            var stagedPath = Path.Combine(projectRoot, ".pre-commit.test.tmp");

            using var stream = HookCommandRunner.CreateStagedHookFileStream(stagedPath);

            Assert.True(stream.CanWrite);
            AssertPrivateFileMode(stagedPath);
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

    private static void AssertNoHookTempFiles(string hooksDir)
        => Assert.Empty(Directory.GetFiles(hooksDir, ".pre-commit.*.tmp"));

    private static void AssertPrivateFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        var mode = File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits;
        Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
    }

    private static string QuoteShellForTest(string value)
        => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
