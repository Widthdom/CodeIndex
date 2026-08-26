using CodeIndex.Database;
using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Cli;

internal sealed class TrustedGitHubCliUnavailableException(string message) : InvalidOperationException(message);

internal static class GitHubCliExecutableResolver
{
    internal const string ExecutableEnvironmentVariable = "CDIDX_GH_EXECUTABLE";

    private const string UnavailableMessage =
        "Could not resolve a trusted GitHub CLI executable. Install gh in a standard location or set CDIDX_GH_EXECUTABLE to a trusted absolute path (gh.exe on Windows).";
    private const int MaxHomebrewCellarVersions = 32;
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly AsyncLocal<IReadOnlyList<string>?> CandidatePathsOverride = new();
    private static readonly AsyncLocal<Func<string, CancellationToken, bool>?> VersionProbeOverride = new();

    internal static IReadOnlyList<string>? CandidatePathsForTesting
    {
        get => CandidatePathsOverride.Value;
        set => CandidatePathsOverride.Value = value;
    }

    internal static Func<string, CancellationToken, bool>? VersionProbeForTesting
    {
        get => VersionProbeOverride.Value;
        set => VersionProbeOverride.Value = value;
    }

    internal static string ResolvePathOrThrow(CancellationToken cancellationToken = default)
        => Resolve(cancellationToken).Path ?? throw new TrustedGitHubCliUnavailableException(UnavailableMessage);

    internal static GitExecutableStatus GetStatus(CancellationToken cancellationToken = default)
        => Resolve(cancellationToken).Status;

    internal static IReadOnlyList<ExtensionTrustOverride> GetAcceptedTrustOverrides(GitExecutableStatus status)
    {
        if (!status.Accepted || !string.Equals(status.Source, "environment_override", StringComparison.Ordinal))
            return [];

        var modeDetail = status.UnixMode == null
            ? "regular non-reparse executable with trusted owner/write ACLs and ancestors"
            : $"{status.Owner ?? "trusted"}-owned, owner-only-writable mode {status.UnixMode} executable with trusted ancestors";
        return
        [
            new ExtensionTrustOverride(
                "github_cli_executable",
                ExecutableEnvironmentVariable,
                status.Path ?? string.Empty,
                status.Path,
                $"Absolute GitHub CLI executable override accepted after {modeDetail} validation.")
        ];
    }

    internal static IReadOnlyList<string> KnownCandidatePathsForTests()
        => EnumerateKnownCandidatePaths().ToList();

    internal static IReadOnlyList<string> HomebrewCellarCandidatePathsForTests(string prefix)
        => EnumerateHomebrewCellarCandidatePaths(prefix).ToList();

    internal static GitExecutableStatus EvaluateCandidateForTesting(
        string path,
        bool probeVersion = false)
        => EvaluateCandidate(path, "test_candidate", probeVersion, CancellationToken.None).Status;

    private static GitHubCliExecutableResolution Resolve(CancellationToken cancellationToken)
    {
        var environmentValue = EnvironmentAccess.GetProcessEnvironmentVariable(ExecutableEnvironmentVariable);
        if (environmentValue != null)
            return EvaluateCandidate(environmentValue, "environment_override", probeVersion: true, cancellationToken);

        var candidates = CandidatePathsOverride.Value ?? EnumerateKnownCandidatePaths().ToList();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = EvaluateCandidate(candidate, "known_location", probeVersion: true, cancellationToken);
            if (resolution.Path != null)
                return resolution;
        }

        return new GitHubCliExecutableResolution(
            Path: null,
            new GitExecutableStatus(
                "known_location",
                Accepted: false,
                "no_trusted_candidate",
                Path: null,
                OwnerOnlyWritable: null,
                UnixMode: null,
                Executable: null,
                Owner: null,
                OwnerTrusted: null,
                AncestorDirectoriesTrusted: null));
    }

    private static GitHubCliExecutableResolution EvaluateCandidate(
        string path,
        string source,
        bool probeVersion,
        CancellationToken cancellationToken)
    {
        var validation = TrustedExecutableValidator.Evaluate(
            path,
            source,
            expectedUnixFileName: "gh",
            expectedWindowsFileName: "gh.exe",
            executionProbe: probeVersion
                ? executablePath => ProbeVersion(executablePath, cancellationToken)
                : null,
            allowMacHomebrewAdminGroupWrite: IsMacHomebrewCellarGhPath(path));
        return new GitHubCliExecutableResolution(
            validation.Path,
            new GitExecutableStatus(
                validation.Source,
                validation.Accepted,
                validation.Reason,
                validation.DiagnosticPath,
                validation.OwnerOnlyWritable,
                validation.UnixMode,
                validation.Executable,
                validation.Owner,
                validation.OwnerTrusted,
                validation.AncestorDirectoriesTrusted));
    }

    private static IEnumerable<string> EnumerateKnownCandidatePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     })
            {
                if (string.IsNullOrWhiteSpace(root))
                    continue;

                yield return root == Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                    ? Path.Combine(root, "Programs", "GitHub CLI", "gh.exe")
                    : Path.Combine(root, "GitHub CLI", "gh.exe");
            }
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin/gh";
            foreach (var candidate in EnumerateHomebrewCellarCandidatePaths("/opt/homebrew"))
                yield return candidate;
            yield return "/usr/local/bin/gh";
            foreach (var candidate in EnumerateHomebrewCellarCandidatePaths("/usr/local"))
                yield return candidate;
            yield return "/usr/bin/gh";
            yield break;
        }

        yield return "/usr/bin/gh";
        yield return "/usr/local/bin/gh";
        yield return "/bin/gh";
    }

    private static IEnumerable<string> EnumerateHomebrewCellarCandidatePaths(string prefix)
    {
        var formulaDirectory = Path.Combine(prefix, "Cellar", "gh");
        IEnumerable<string> versionDirectories;
        try
        {
            versionDirectories = Directory
                .EnumerateDirectories(formulaDirectory)
                .Take(MaxHomebrewCellarVersions)
                .OrderByDescending(static path => path, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var versionDirectory in versionDirectories)
            yield return Path.Combine(versionDirectory, "bin", "gh");
    }

    private static bool IsMacHomebrewCellarGhPath(string path)
    {
        if (!OperatingSystem.IsMacOS() || !Path.IsPathFullyQualified(path))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return IsPathUnderHomebrewGhCellar(fullPath, "/opt/homebrew")
               || IsPathUnderHomebrewGhCellar(fullPath, "/usr/local");
    }

    private static bool IsPathUnderHomebrewGhCellar(string path, string prefix)
    {
        var formulaDirectory = Path.Combine(prefix, "Cellar", "gh") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(formulaDirectory, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(path), "gh", StringComparison.Ordinal))
        {
            return false;
        }

        var relative = path[formulaDirectory.Length..];
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 && string.Equals(segments[1], "bin", StringComparison.Ordinal);
    }

    private static bool ProbeVersion(string executablePath, CancellationToken cancellationToken)
    {
        var probeOverride = VersionProbeOverride.Value;
        if (probeOverride != null)
            return probeOverride(executablePath, cancellationToken);

        var workingDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrEmpty(workingDirectory))
            return false;

        var startInfo = ProcessLaunchPolicy.CreateNoShellStartInfo(
            executablePath,
            workingDirectory,
            redirectStandardOutput: true,
            redirectStandardError: true,
            createNoWindow: true);
        startInfo.ArgumentList.Add("--version");
        SubprocessEnvironmentPolicy.ApplyUpgradeInstallerEnvironment(startInfo);
        var result = ProgramRunner.RunInstallerProcessDetailed(
            startInfo,
            VersionProbeTimeout,
            cancellationToken,
            suppressOutput: true);
        return result.ExitCode == CommandExitCodes.Success
               && !result.OutputTruncated
               && result.StdoutTail?.TrimStart().StartsWith("gh version ", StringComparison.OrdinalIgnoreCase) == true;
    }

    private sealed record GitHubCliExecutableResolution(string? Path, GitExecutableStatus Status);
}
