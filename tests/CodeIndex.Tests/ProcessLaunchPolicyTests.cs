using CodeIndex;

namespace CodeIndex.Tests;

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
}
