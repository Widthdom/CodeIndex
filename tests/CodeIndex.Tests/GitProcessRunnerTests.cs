using System.Diagnostics;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class GitProcessRunnerTests : IDisposable
{
    private readonly string tempDir;

    public GitProcessRunnerTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_git_process_runner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
            TestProjectHelper.DeleteDirectory(tempDir);
    }

    [Fact]
    public void RunCapturingResult_PreservesExitDiagnosticContract_Issue4179()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(tempDir, "repo");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(tempDir, "fake-git");
        Directory.CreateDirectory(fakeGitDir);
        var fakeGit = WriteFakeGitThatFailsWithLongSensitiveStderr(fakeGitDir);
        var psi = new ProcessStartInfo
        {
            FileName = fakeGit,
            WorkingDirectory = repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var result = GitProcessRunner.RunCapturingResult(psi, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(result.HasValue);
        var value = result.Value;
        Assert.Equal(23, value.ExitCode);
        Assert.Equal(GitCommandFailureKind.ExitCode, value.FailureKind);
        Assert.Equal(value.Diagnostic, value.Error);
        Assert.Contains("[redacted]", value.Diagnostic!);
        Assert.DoesNotContain("/Users/example/private", value.Diagnostic!);
    }

    private static string WriteFakeGitThatFailsWithLongSensitiveStderr(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, """
#!/bin/sh
perl -e 'print STDERR "/Users/example/private/repo/.git/config " . ("x" x 2000)'
exit 23
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
