using CodeIndex.Cli;
using System.Runtime.Versioning;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class GitHubCliExecutableResolverTests
{
    [Fact]
    public void KnownCandidates_AreAbsoluteExpectedNamesAndNeverUseCurrentDirectory_Issue5184()
    {
        var candidates = GitHubCliExecutableResolver.KnownCandidatePathsForTests();

        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate =>
        {
            Assert.True(Path.IsPathFullyQualified(candidate));
            Assert.Equal(
                OperatingSystem.IsWindows() ? "gh.exe" : "gh",
                Path.GetFileName(candidate),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            Assert.NotEqual(Path.GetFullPath("gh"), candidate);
        });
    }

    [Fact]
    public void HomebrewCellarCandidates_EnumerateVersionedRegularTargetsWithoutLaunchingBinSymlinks_Issue5184()
    {
        var prefix = TestProjectHelper.CreateTempProject("cdidx_gh_homebrew_5184");
        try
        {
            var older = Path.Combine(prefix, "Cellar", "gh", "2.94.0", "bin", "gh");
            var newer = Path.Combine(prefix, "Cellar", "gh", "2.95.0", "bin", "gh");
            Directory.CreateDirectory(Path.GetDirectoryName(older)!);
            Directory.CreateDirectory(Path.GetDirectoryName(newer)!);
            File.WriteAllText(older, "older");
            File.WriteAllText(newer, "newer");

            var candidates = GitHubCliExecutableResolver.HomebrewCellarCandidatePathsForTests(prefix);

            Assert.Equal([newer, older], candidates);
            Assert.All(candidates, candidate =>
            {
                Assert.True(Path.IsPathFullyQualified(candidate));
                Assert.Equal("gh", Path.GetFileName(candidate));
                Assert.Contains(
                    $"{Path.DirectorySeparatorChar}Cellar{Path.DirectorySeparatorChar}gh{Path.DirectorySeparatorChar}",
                    candidate,
                    StringComparison.Ordinal);
            });
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(prefix);
        }
    }

    [ExternalProcessFact]
    public void PathFirstSubstitute_IsNeverExecutedWhenNoTrustedVerifierExists_Issue5184()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_gh_path_substitute_5184");
        var markerPath = Path.Combine(root, "executed.marker");
        var fakePathDirectory = Path.Combine(root, "path-first");
        Directory.CreateDirectory(fakePathDirectory);
        var fakeGhPath = Path.Combine(fakePathDirectory, "gh");
        File.WriteAllText(fakeGhPath, $"#!/bin/sh\nprintf executed > '{markerPath}'\nexit 0\n");
        File.SetUnixFileMode(
            fakeGhPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var env = EnvironmentVariableScope.Capture(
            "PATH",
            GitHubCliExecutableResolver.ExecutableEnvironmentVariable);
        var oldCandidates = GitHubCliExecutableResolver.CandidatePathsForTesting;
        var oldProbe = GitHubCliExecutableResolver.VersionProbeForTesting;
        env.Set("PATH", fakePathDirectory);
        env.Set(GitHubCliExecutableResolver.ExecutableEnvironmentVariable, null);
        GitHubCliExecutableResolver.CandidatePathsForTesting = [];
        GitHubCliExecutableResolver.VersionProbeForTesting = null;
        try
        {
            var assetPath = Path.Combine(root, "asset.txt");
            File.WriteAllText(assetPath, "asset");

            Assert.Throws<TrustedGitHubCliUnavailableException>(
                () => ProgramRunner.CreateUpgradeAttestationStartInfo(assetPath, "v9.9.9"));
            Assert.False(File.Exists(markerPath));
        }
        finally
        {
            GitHubCliExecutableResolver.CandidatePathsForTesting = oldCandidates;
            GitHubCliExecutableResolver.VersionProbeForTesting = oldProbe;
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [ExternalProcessFact]
    public void ValidTrustedVerifier_IsPinnedAndReceivesExactAttestationArguments_Issue5184()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_gh_attestation_5184");
        var argsPath = Path.Combine(root, "args.txt");
        var ghPath = Path.Combine(root, "gh");
        var assetPath = Path.Combine(root, "release asset.txt");
        File.WriteAllText(assetPath, "asset");
        File.WriteAllText(
            ghPath,
            $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then
              printf 'gh version 2.99.0\n'
              exit 0
            fi
            : > '{argsPath}'
            for arg in "$@"; do
              printf '%s\n' "$arg" >> '{argsPath}'
            done
            exit 0
            """);
        File.SetUnixFileMode(
            ghPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var env = EnvironmentVariableScope.Capture(
            GitHubCliExecutableResolver.ExecutableEnvironmentVariable);
        env.Set(GitHubCliExecutableResolver.ExecutableEnvironmentVariable, ghPath);
        try
        {
            var startInfo = ProgramRunner.CreateUpgradeAttestationStartInfo(assetPath, "v9.9.9");
            var result = ProgramRunner.RunInstallerProcessDetailed(
                startInfo,
                TimeSpan.FromSeconds(10),
                suppressOutput: true);

            Assert.True(TrustedExecutableValidator.TryResolveRealUnixPath(ghPath, out var canonicalGhPath));
            Assert.Equal(CommandExitCodes.Success, result.ExitCode);
            Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
            Assert.Equal(canonicalGhPath, startInfo.FileName);
            Assert.Equal(
                [
                    "attestation",
                    "verify",
                    Path.GetFullPath(assetPath),
                    "-R",
                    "Widthdom/CodeIndex",
                    "--signer-workflow",
                    "github.com/Widthdom/CodeIndex/.github/workflows/release.yml",
                    "--source-ref",
                    "refs/tags/v9.9.9",
                ],
                File.ReadAllLines(argsPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void EnvironmentOverride_HasFailClosedPrecedenceAndProducesTrustDiagnostics_Issue5184()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_gh_override_5184");
        var ghPath = CreatePosixGh(root);
        using var env = EnvironmentVariableScope.Capture(
            GitHubCliExecutableResolver.ExecutableEnvironmentVariable);
        var oldCandidates = GitHubCliExecutableResolver.CandidatePathsForTesting;
        var oldProbe = GitHubCliExecutableResolver.VersionProbeForTesting;
        var probeCount = 0;
        GitHubCliExecutableResolver.CandidatePathsForTesting = [ghPath];
        GitHubCliExecutableResolver.VersionProbeForTesting = (_, _) =>
        {
            probeCount++;
            return true;
        };
        try
        {
            env.Set(GitHubCliExecutableResolver.ExecutableEnvironmentVariable, "gh");
            var rejected = GitHubCliExecutableResolver.GetStatus();

            Assert.Equal("environment_override", rejected.Source);
            Assert.False(rejected.Accepted);
            Assert.Equal("path_not_absolute", rejected.Reason);
            Assert.Equal(0, probeCount);
            Assert.Empty(GitHubCliExecutableResolver.GetAcceptedTrustOverrides(rejected));

            env.Set(GitHubCliExecutableResolver.ExecutableEnvironmentVariable, ghPath);
            var accepted = GitHubCliExecutableResolver.GetStatus();
            var trustOverride = Assert.Single(
                GitHubCliExecutableResolver.GetAcceptedTrustOverrides(accepted));

            Assert.True(accepted.Accepted);
            Assert.Equal("accepted", accepted.Reason);
            Assert.Equal("gh", accepted.Path);
            Assert.Equal(1, probeCount);
            Assert.Equal("github_cli_executable", trustOverride.Kind);
            Assert.Equal(
                GitHubCliExecutableResolver.ExecutableEnvironmentVariable,
                trustOverride.EnvironmentVariable);
            Assert.Contains("mode 0700", trustOverride.Message, StringComparison.Ordinal);
        }
        finally
        {
            GitHubCliExecutableResolver.CandidatePathsForTesting = oldCandidates;
            GitHubCliExecutableResolver.VersionProbeForTesting = oldProbe;
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void GetStatus_PropagatesCancellationIntoVersionProbe_Issue5184()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_gh_status_cancel_5184");
        var ghPath = CreatePosixGh(root);
        using var env = EnvironmentVariableScope.Capture(
            GitHubCliExecutableResolver.ExecutableEnvironmentVariable);
        var oldProbe = GitHubCliExecutableResolver.VersionProbeForTesting;
        using var cancellation = new CancellationTokenSource();
        env.Set(GitHubCliExecutableResolver.ExecutableEnvironmentVariable, ghPath);
        GitHubCliExecutableResolver.VersionProbeForTesting = (_, token) =>
        {
            Assert.True(token.CanBeCanceled);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return true;
        };
        try
        {
            Assert.Throws<OperationCanceledException>(
                () => GitHubCliExecutableResolver.GetStatus(cancellation.Token));
        }
        finally
        {
            GitHubCliExecutableResolver.VersionProbeForTesting = oldProbe;
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void PosixCandidateValidation_RejectsSymlinkModeExecuteTypeAndAncestorFailures_Issue5184()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_gh_validation_5184");
        try
        {
            var ghPath = CreatePosixGh(root);
            var accepted = GitHubCliExecutableResolver.EvaluateCandidateForTesting(ghPath);
            Assert.True(accepted.Accepted);
            Assert.True(accepted.OwnerOnlyWritable);
            Assert.Equal("0700", accepted.UnixMode);
            Assert.True(accepted.Executable);
            Assert.True(accepted.OwnerTrusted);
            Assert.True(accepted.AncestorDirectoriesTrusted);

            File.SetUnixFileMode(
                ghPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupWrite);
            Assert.Equal(
                "shared_writable",
                GitHubCliExecutableResolver.EvaluateCandidateForTesting(ghPath).Reason);

            File.SetUnixFileMode(ghPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(
                "not_executable",
                GitHubCliExecutableResolver.EvaluateCandidateForTesting(ghPath).Reason);

            var directoryCandidate = Path.Combine(root, "directory", "gh");
            Directory.CreateDirectory(directoryCandidate);
            Assert.Equal(
                "not_regular_file",
                GitHubCliExecutableResolver.EvaluateCandidateForTesting(directoryCandidate).Reason);

            var target = Path.Combine(root, "target");
            File.WriteAllText(target, "target");
            var symlinkCandidate = Path.Combine(root, "symlink", "gh");
            Directory.CreateDirectory(Path.GetDirectoryName(symlinkCandidate)!);
            File.CreateSymbolicLink(symlinkCandidate, target);
            Assert.Equal(
                "symlink_or_reparse_point",
                GitHubCliExecutableResolver.EvaluateCandidateForTesting(symlinkCandidate).Reason);

            var unsafeDirectory = Path.Combine(root, "unsafe-ancestor");
            Directory.CreateDirectory(unsafeDirectory);
            var unsafeGh = CreatePosixGh(unsafeDirectory);
            File.SetUnixFileMode(
                unsafeDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
            Assert.Equal(
                "ancestor_untrusted",
                GitHubCliExecutableResolver.EvaluateCandidateForTesting(unsafeGh).Reason);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void WindowsCandidateValidation_RequiresTrustedAclAndExecutableImage_Issue5184()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTrustedWindowsGitDirectory("cdidx_windows_gh_5184");
        var ghPath = Path.Combine(root, "gh.exe");
        try
        {
            File.WriteAllText(ghPath, "not a PE image");
            var invalidImage = GitHubCliExecutableResolver.EvaluateCandidateForTesting(ghPath);
            Assert.False(invalidImage.Accepted);
            Assert.Equal("invalid_executable_format", invalidImage.Reason);

            File.Copy(Environment.ProcessPath!, ghPath, overwrite: true);
            var oldProbe = GitHubCliExecutableResolver.VersionProbeForTesting;
            GitHubCliExecutableResolver.VersionProbeForTesting = (_, _) => true;
            try
            {
                var accepted = GitHubCliExecutableResolver.EvaluateCandidateForTesting(
                    ghPath,
                    probeVersion: true);
                Assert.True(accepted.Accepted);
                Assert.True(accepted.OwnerTrusted);
                Assert.True(accepted.AncestorDirectoriesTrusted);
                Assert.True(accepted.Executable);
            }
            finally
            {
                GitHubCliExecutableResolver.VersionProbeForTesting = oldProbe;
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static string CreatePosixGh(string root)
    {
        Directory.CreateDirectory(root);
        var ghPath = Path.Combine(root, "gh");
        File.WriteAllText(ghPath, "#!/bin/sh\nprintf 'gh version 2.99.0\\n'\n");
        File.SetUnixFileMode(
            ghPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return ghPath;
    }
}
