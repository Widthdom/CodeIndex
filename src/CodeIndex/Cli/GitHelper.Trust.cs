namespace CodeIndex.Cli;

public static partial class GitHelper
{
    private static readonly TimeSpan GitExecutableProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<Func<string, bool>?> GitVersionProbeOverride = new();

    internal static Func<string, bool>? GitVersionProbeForTesting
    {
        get => GitVersionProbeOverride.Value;
        set => GitVersionProbeOverride.Value = value;
    }

    private static bool TryProbeGitVersion(string executablePath)
    {
        var probeOverride = GitVersionProbeOverride.Value;
        if (probeOverride != null)
            return probeOverride(executablePath);

        var workingDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(workingDirectory))
            return false;

        var startInfo = CodeIndex.ProcessLaunchPolicy.CreateNoShellStartInfo(
            fileName: executablePath,
            workingDirectory: workingDirectory,
            redirectStandardOutput: true,
            redirectStandardError: true,
            createNoWindow: true);
        startInfo.ArgumentList.Add("--version");
        CodeIndex.SubprocessEnvironmentPolicy.ApplyGitEnvironment(startInfo);

        var result = GitProcessRunner.RunCapturingResult(startInfo, GitExecutableProbeTimeout);
        return result is { ExitCode: 0, FailureKind: GitCommandFailureKind.None }
               && result.Value.Output.Trim().StartsWith("git version ", StringComparison.OrdinalIgnoreCase);
    }

}
