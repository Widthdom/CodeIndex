using CodeIndex;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class ProcessLaunchPolicyTests
{
    [Fact]
    public void CreateNoShellStartInfo_SetsExplicitLaunchContract_Issue3991()
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
    }

    [Fact]
    public void AddInvariantIntArgument_AppendsProtocolArgumentPair_Issue3991()
    {
        var startInfo = ProcessLaunchPolicy.CreateNoShellStartInfo();

        ProcessLaunchPolicy.AddInvariantIntArgument(startInfo, "--max-line-bytes", 8192);

        Assert.Equal(["--max-line-bytes", "8192"], startInfo.ArgumentList.ToArray());
    }

    [Fact]
    public void CreateUtf8RedirectedWorkerStartInfo_DisablesShellAndUsesUtf8NoBom_Issue3991()
    {
        var startInfo = ProcessLaunchPolicy.CreateUtf8RedirectedWorkerStartInfo();

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.False(startInfo.StandardInputEncoding!.GetPreamble().Length > 0);
        Assert.False(startInfo.StandardOutputEncoding!.GetPreamble().Length > 0);
        Assert.False(startInfo.StandardErrorEncoding!.GetPreamble().Length > 0);
    }

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
}
