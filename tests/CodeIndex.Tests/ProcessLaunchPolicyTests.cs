using CodeIndex;

namespace CodeIndex.Tests;

public class ProcessLaunchPolicyTests
{
    [Fact]
    public void StartInfoBuilders_SetExplicitArgumentsAndUtf8LaunchContracts_Issues3991_4075()
    {
        var startInfo = ProcessLaunchPolicy.CreateNoShellStartInfo(
            fileName: "/usr/bin/git",
            workingDirectory: "/tmp/work",
            redirectStandardOutput: true,
            redirectStandardError: true,
            createNoWindow: true);

        Assert.Equal("/usr/bin/git", startInfo.FileName);
        Assert.Equal("/tmp/work", startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(string.Empty, startInfo.Arguments);

        var integerArgs = ProcessLaunchPolicy.CreateNoShellStartInfo();
        ProcessLaunchPolicy.AddInvariantIntArgument(integerArgs, "--max-line-bytes", 8192);

        Assert.Equal(["--max-line-bytes", "8192"], integerArgs.ArgumentList.ToArray());

        var workerArgs = ProcessLaunchPolicy.CreateNoShellStartInfo();
        ProcessLaunchPolicy.AddWorkerCommandArguments(
            workerArgs,
            "__cdidx-worker",
            16384,
            "/tmp/hook.dll",
            "Demo.Hook");

        Assert.Equal(
            [
                "__cdidx-worker",
                "/tmp/hook.dll",
                "Demo.Hook",
                ProcessLaunchPolicy.WorkerProtocolMaxLineBytesOption,
                "16384",
            ],
            workerArgs.ArgumentList.ToArray());
        Assert.Equal(string.Empty, workerArgs.Arguments);

        var utf8Worker = ProcessLaunchPolicy.CreateUtf8RedirectedWorkerStartInfo();
        Assert.False(utf8Worker.UseShellExecute);
        Assert.True(utf8Worker.RedirectStandardInput);
        Assert.True(utf8Worker.RedirectStandardOutput);
        Assert.True(utf8Worker.RedirectStandardError);
        Assert.True(utf8Worker.CreateNoWindow);
        Assert.Empty(utf8Worker.StandardInputEncoding!.GetPreamble());
        Assert.Empty(utf8Worker.StandardOutputEncoding!.GetPreamble());
        Assert.Empty(utf8Worker.StandardErrorEncoding!.GetPreamble());
    }
}

[Collection("SQLite pool sensitive")]
public class SubprocessEnvironmentPolicyTests
{
    [Fact]
    public void SubprocessEnvironmentPolicy_WorkerOnlyForwardsTestPrefixedEnvironment_Issue3910()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_TEST_SUBPROCESS_POLICY_3910",
            "CDIDX_SECRET_SUBPROCESS_POLICY_3910",
            "HTTPS_PROXY");
        env.Set("CDIDX_TEST_SUBPROCESS_POLICY_3910", "allowed");
        env.Set("CDIDX_SECRET_SUBPROCESS_POLICY_3910", "secret");
        env.Set("HTTPS_PROXY", "http://proxy.example.test:8080");
        var startInfo = ProcessLaunchPolicy.CreateNoShellStartInfo();

        SubprocessEnvironmentPolicy.ApplyIsolatedWorkerEnvironment(startInfo);

        Assert.Equal("allowed", startInfo.Environment["CDIDX_TEST_SUBPROCESS_POLICY_3910"]);
        Assert.False(startInfo.Environment.ContainsKey("CDIDX_SECRET_SUBPROCESS_POLICY_3910"));
        Assert.False(startInfo.Environment.ContainsKey("HTTPS_PROXY"));
    }

    [Fact]
    public void SubprocessEnvironmentPolicy_GitKeepsProxyAndGitKnobsWithoutCdidxSecrets_Issue3910()
    {
        using var env = EnvironmentVariableScope.Capture(
            "HTTPS_PROXY",
            "GIT_CONFIG_NOSYSTEM",
            "CDIDX_TEST_SUBPROCESS_POLICY_3910",
            "CDIDX_SECRET_SUBPROCESS_POLICY_3910");
        env.Set("HTTPS_PROXY", "http://proxy.example.test:8080");
        env.Set("GIT_CONFIG_NOSYSTEM", "1");
        env.Set("CDIDX_TEST_SUBPROCESS_POLICY_3910", "test-only");
        env.Set("CDIDX_SECRET_SUBPROCESS_POLICY_3910", "secret");
        var startInfo = ProcessLaunchPolicy.CreateNoShellStartInfo();

        SubprocessEnvironmentPolicy.ApplyGitEnvironment(startInfo);

        Assert.Equal("http://proxy.example.test:8080", startInfo.Environment["HTTPS_PROXY"]);
        Assert.Equal("1", startInfo.Environment["GIT_CONFIG_NOSYSTEM"]);
        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.False(startInfo.Environment.ContainsKey("CDIDX_TEST_SUBPROCESS_POLICY_3910"));
        Assert.False(startInfo.Environment.ContainsKey("CDIDX_SECRET_SUBPROCESS_POLICY_3910"));
    }

    [Fact]
    public void SubprocessEnvironmentPolicy_UpgradeForwardsBoundedNetworkSettings_Issue4604()
    {
        using var env = EnvironmentVariableScope.Capture(
            "CDIDX_NETWORK_CONNECT_TIMEOUT_SECONDS",
            "CDIDX_NETWORK_LOW_SPEED_LIMIT_BYTES",
            "CDIDX_NETWORK_LOW_SPEED_TIME_SECONDS",
            "CDIDX_NETWORK_MAX_TIME_SECONDS",
            "CDIDX_NETWORK_RETRY_COUNT",
            "CDIDX_NETWORK_RETRY_DELAY_SECONDS",
            "CDIDX_SECRET_SUBPROCESS_POLICY_4604");
        env.Set("CDIDX_NETWORK_CONNECT_TIMEOUT_SECONDS", "12");
        env.Set("CDIDX_NETWORK_LOW_SPEED_LIMIT_BYTES", "2048");
        env.Set("CDIDX_NETWORK_LOW_SPEED_TIME_SECONDS", "45");
        env.Set("CDIDX_NETWORK_MAX_TIME_SECONDS", "240");
        env.Set("CDIDX_NETWORK_RETRY_COUNT", "3");
        env.Set("CDIDX_NETWORK_RETRY_DELAY_SECONDS", "2");
        env.Set("CDIDX_SECRET_SUBPROCESS_POLICY_4604", "secret");
        var startInfo = ProcessLaunchPolicy.CreateNoShellStartInfo();

        SubprocessEnvironmentPolicy.ApplyUpgradeInstallerEnvironment(startInfo);

        Assert.Equal("12", startInfo.Environment["CDIDX_NETWORK_CONNECT_TIMEOUT_SECONDS"]);
        Assert.Equal("2048", startInfo.Environment["CDIDX_NETWORK_LOW_SPEED_LIMIT_BYTES"]);
        Assert.Equal("45", startInfo.Environment["CDIDX_NETWORK_LOW_SPEED_TIME_SECONDS"]);
        Assert.Equal("240", startInfo.Environment["CDIDX_NETWORK_MAX_TIME_SECONDS"]);
        Assert.Equal("3", startInfo.Environment["CDIDX_NETWORK_RETRY_COUNT"]);
        Assert.Equal("2", startInfo.Environment["CDIDX_NETWORK_RETRY_DELAY_SECONDS"]);
        Assert.False(startInfo.Environment.ContainsKey("CDIDX_SECRET_SUBPROCESS_POLICY_4604"));
    }
}
