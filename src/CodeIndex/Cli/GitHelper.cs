using System.Diagnostics;
using System.Text;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Cli;

public enum GitRepositoryType
{
    None,
    Normal,
    Worktree,
    Bare,
}

public enum GitHeadCommitState
{
    None,
    NotARepo,
    DetachedHead,
    Resolved,
    Error,
}

public enum GitCommandFailureKind
{
    None,
    TrustedGitUnavailable,
    StartFailed,
    ExitCode,
    TimedOut,
    Cancelled,
    CaptureLimitExceeded,
    CaptureFailed,
    OutputCaptureIncomplete,
    Exception,
}

public sealed record GitHeadCommitResult(
    GitHeadCommitState State,
    string? Sha = null,
    string? Reason = null,
    GitCommandFailureKind FailureKind = GitCommandFailureKind.None,
    string? Diagnostic = null)
{
    public static GitHeadCommitResult None { get; } = new(GitHeadCommitState.None);
    public static GitHeadCommitResult NotARepo { get; } = new(GitHeadCommitState.NotARepo);
    public static GitHeadCommitResult DetachedHead(string sha) => new(GitHeadCommitState.DetachedHead, sha);
    public static GitHeadCommitResult Resolved(string sha) => new(GitHeadCommitState.Resolved, sha);
    public static GitHeadCommitResult Error(
        string reason,
        GitCommandFailureKind failureKind = GitCommandFailureKind.Exception,
        string? diagnostic = null)
        => new(GitHeadCommitState.Error, Reason: reason, FailureKind: failureKind, Diagnostic: diagnostic);
}

/// <summary>
/// Git integration helpers.
/// Git連携ヘルパー。
/// </summary>
public static partial class GitHelper
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public const string GitExecutableEnvironmentVariable = "CDIDX_GIT_EXECUTABLE";

    internal static Func<string, bool>? FileSystemIgnoreCaseProbeForTesting { get; set; }

    internal const int MaxGitMetadataFileBytes = 4 * 1024;

    public sealed record WorktreeStatus(bool IsDirty, IReadOnlyList<string> UnresolvedMergeFiles);

    private static readonly HashSet<string> UnresolvedMergeStatuses = new(StringComparer.Ordinal)
    {
        "DD",
        "AU",
        "UD",
        "UA",
        "DU",
        "AA",
        "UU",
    };

    internal const int MaxCapturedGitOutputChars = GitProcessRunner.MaxCapturedGitOutputChars;
    internal const int MaxGitFailureDiagnosticChars = GitProcessRunner.MaxGitFailureDiagnosticChars;
    private static readonly TimeSpan DefaultGitCommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly AsyncLocal<TimeSpan?> GitCommandTimeoutOverride = new();
    internal static TimeSpan GitCommandTimeout
    {
        get => GitCommandTimeoutOverride.Value ?? DefaultGitCommandTimeout;
        set => GitCommandTimeoutOverride.Value = value;
    }

    // CodeQL treats identifiers containing "trusted" as secret-bearing. These values are
    // validated executable paths, not secrets, so keep the internal data-flow names explicit.
    private static readonly Lazy<GitExecutableResolution> ValidatedGitExecutable = new(ResolveValidatedGitExecutableFromKnownLocations);
    private static readonly AsyncLocal<string?> GitExecutablePathOverrideValue = new();
    internal static string? GitExecutablePathOverride
    {
        get => GitExecutablePathOverrideValue.Value;
        set => GitExecutablePathOverrideValue.Value = value;
    }

    private const string ValidatedGitUnavailableMessage =
        "Could not resolve a trusted git executable path. Install git in a standard system location or set CDIDX_GIT_EXECUTABLE to a trusted absolute path. / 信頼済みの git 実行ファイルパスを解決できませんでした。標準のシステム場所に git をインストールするか、CDIDX_GIT_EXECUTABLE に信頼できる絶対パスを設定してください。";

    private sealed record GitExecutableResolution(string? Path, GitExecutableStatus Status);

    private static ProcessStartInfo? TryCreateGitStartInfo(string projectRoot)
    {
        var gitExecutablePath = TryResolveGitExecutablePath();
        if (gitExecutablePath == null)
            return null;

        var startInfo = CodeIndex.ProcessLaunchPolicy.CreateNoShellStartInfo(
            fileName: gitExecutablePath,
            workingDirectory: projectRoot,
            redirectStandardOutput: true,
            redirectStandardError: true,
            createNoWindow: true);
        startInfo.StandardOutputEncoding = Utf8NoBom;
        startInfo.StandardErrorEncoding = Utf8NoBom;
        CodeIndex.SubprocessEnvironmentPolicy.ApplyGitEnvironment(startInfo);
        return startInfo;
    }

    private static ProcessStartInfo CreateGitStartInfoOrThrow(string projectRoot)
        => TryCreateGitStartInfo(projectRoot) ?? throw new InvalidOperationException(ValidatedGitUnavailableMessage);

    private static string? TryResolveGitExecutablePath()
    {
        var overridePath = NormalizeValidatedGitExecutablePath(GitExecutablePathOverrideValue.Value);
        if (overridePath != null)
            return overridePath;

        var environmentValue = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(GitExecutableEnvironmentVariable);
        if (environmentValue != null)
            return EvaluateGitExecutableCandidate(environmentValue, "environment_override").Path;

        return ValidatedGitExecutable.Value.Path;
    }

    internal static string? TryResolveGitExecutablePathForHook()
        => TryResolveGitExecutablePath();

    internal static bool TryValidatePinnedGitExecutablePathForHook(
        string path,
        out string canonicalPath)
    {
        var resolution = EvaluateGitExecutableCandidate(
            path,
            "hook_manifest",
            probeVersion: false);
        canonicalPath = resolution.Path ?? string.Empty;
        return resolution.Path != null;
    }

    public static GitExecutableStatus GetGitExecutableStatus()
    {
        var overridePath = NormalizeValidatedGitExecutablePath(GitExecutablePathOverrideValue.Value);
        if (overridePath != null)
        {
            return new GitExecutableStatus(
                "test_override",
                Accepted: true,
                "accepted",
                DiagnosticSanitizer.ForPath(overridePath),
                OwnerOnlyWritable: null,
                UnixMode: null,
                Executable: null,
                Owner: null,
                OwnerTrusted: null,
                AncestorDirectoriesTrusted: null);
        }

        var environmentValue = global::CodeIndex.EnvironmentAccess.GetProcessEnvironmentVariable(GitExecutableEnvironmentVariable);
        return environmentValue != null
            ? EvaluateGitExecutableCandidate(environmentValue, "environment_override").Status
            : ValidatedGitExecutable.Value.Status;
    }

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
                "git_executable",
                GitExecutableEnvironmentVariable,
                status.Path ?? string.Empty,
                status.Path,
                $"Absolute Git executable override accepted after {modeDetail} validation.")
        ];
    }

    private static GitExecutableResolution ResolveValidatedGitExecutableFromKnownLocations()
    {
        foreach (var candidate in EnumerateValidatedGitExecutableCandidates())
        {
            var resolution = EvaluateGitExecutableCandidate(candidate, "known_location");
            if (resolution.Path != null)
                return resolution;
        }

        return RejectedGitExecutable(
            "known_location",
            "no_trusted_candidate",
            path: null,
            ownerOnlyWritable: null,
            unixMode: null,
            executable: null,
            owner: null,
            ownerTrusted: null,
            ancestorDirectoriesTrusted: null);
    }

    private static IEnumerable<string> EnumerateValidatedGitExecutableCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
                yield return Path.Combine(programFiles, "Git", "bin", "git.exe");
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "Git", "cmd", "git.exe");
                yield return Path.Combine(programFilesX86, "Git", "bin", "git.exe");
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(windows))
                yield return Path.Combine(windows, "System32", "git.exe");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Developer/CommandLineTools/usr/bin/git";
            yield return "/Applications/Xcode.app/Contents/Developer/usr/bin/git";
            yield break;
        }

        yield return "/usr/bin/git";
        yield return "/bin/git";
    }

    internal static IReadOnlyList<string> ValidatedGitExecutableCandidatePathsForTests()
        => EnumerateValidatedGitExecutableCandidates().ToList();

    private static string? NormalizeValidatedGitExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            if (!Path.IsPathFullyQualified(path))
                return null;

            var fullPath = Path.GetFullPath(path);
            if (fullPath.IndexOfAny(['\r', '\n']) >= 0)
                return null;
            if (!HasExpectedGitExecutableName(fullPath))
                return null;

            return File.Exists(LongPath.EnsureWindowsPrefix(fullPath)) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static GitExecutableResolution EvaluateGitExecutableCandidate(
        string path,
        string source,
        bool probeVersion = true)
    {
        var validation = TrustedExecutableValidator.Evaluate(
            path,
            source,
            expectedUnixFileName: "git",
            expectedWindowsFileName: "git.exe",
            executionProbe: probeVersion ? TryProbeGitVersion : null);
        return new GitExecutableResolution(
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

    private static GitExecutableResolution RejectedGitExecutable(
        string source,
        string reason,
        string? path,
        bool? ownerOnlyWritable,
        string? unixMode,
        bool? executable,
        string? owner,
        bool? ownerTrusted,
        bool? ancestorDirectoriesTrusted)
        => new(
            Path: null,
            new GitExecutableStatus(
                source,
                Accepted: false,
                reason,
                path,
                ownerOnlyWritable,
                unixMode,
                executable,
                owner,
                ownerTrusted,
                ancestorDirectoriesTrusted));

    private static bool HasExpectedGitExecutableName(string path)
        => string.Equals(
            Path.GetFileName(path),
            OperatingSystem.IsWindows() ? "git.exe" : "git",
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Resolve the common git directory for a project root, handling both normal repos and worktrees.
    /// プロジェクトルートの共通gitディレクトリを解決する。通常リポジトリとworktreeの両方に対応。
    /// In a normal repo, .git is a directory and is returned directly.
    /// In a worktree, .git is a file containing "gitdir: path/to/.git/worktrees/name".
    /// The common dir is resolved via the "commondir" file inside the worktree git dir.
    /// </summary>
    public static string? ResolveGitCommonDir(string projectRoot, CancellationToken cancellationToken = default)
    {
        var dotGit = Path.Combine(projectRoot, ".git");
        var ioDotGit = LongPath.EnsureWindowsPrefix(dotGit);

        // Normal repository: .git is a directory / 通常リポジトリ: .gitがディレクトリ
        if (TryValidateGitMetadataEntry(dotGit, expectDirectory: true, out var canonicalDotGit))
            return canonicalDotGit;

        // Worktree: .git is a file containing "gitdir: <path>" / worktree: .gitがファイルで "gitdir: <path>" を含む
        if (!TryValidateGitMetadataEntry(dotGit, expectDirectory: false, out var canonicalGitFile))
        {
            if (GitMetadataPathExists(ioDotGit))
                return null;
            if (TryGetRepositoryType(projectRoot, cancellationToken) != GitRepositoryType.Bare)
                return null;
            return TryValidateGitMetadataEntry(projectRoot, expectDirectory: true, out var canonicalBareRoot)
                ? canonicalBareRoot
                : null;
        }

        var gitFileContent = DataDirectorySecurity.ReadTextWithinLimit(
            LongPath.EnsureWindowsPrefix(canonicalGitFile),
            MaxGitMetadataFileBytes);
        if (gitFileContent is null)
            return null;

        gitFileContent = gitFileContent.Trim();
        if (!gitFileContent.StartsWith("gitdir:")) return null;

        var worktreeGitDirValue = gitFileContent["gitdir:".Length..].Trim();
        if (!TryResolveGitMetadataPath(projectRoot, worktreeGitDirValue, out var worktreeGitDir))
            return null;
        if (!TryValidateGitMetadataEntry(worktreeGitDir, expectDirectory: true, out worktreeGitDir))
            return null;

        // Read commondir to find the shared .git directory / commondirを読んで共有.gitディレクトリを見つける
        var commonDirFile = Path.Combine(worktreeGitDir, "commondir");
        var ioCommonDirFile = LongPath.EnsureWindowsPrefix(commonDirFile);
        if (GitMetadataPathExists(ioCommonDirFile))
        {
            if (!TryValidateGitMetadataEntry(commonDirFile, expectDirectory: false, out commonDirFile))
                return null;

            var commonDirRelative = DataDirectorySecurity.ReadTextWithinLimit(
                LongPath.EnsureWindowsPrefix(commonDirFile),
                MaxGitMetadataFileBytes);
            if (commonDirRelative is null)
                return null;

            commonDirRelative = commonDirRelative.Trim();
            if (!TryResolveGitMetadataPath(worktreeGitDir, commonDirRelative, out var commonDir))
                return null;
            if (!TryValidateGitMetadataEntry(commonDir, expectDirectory: true, out commonDir))
                return null;
            if (PathCasing.PathsEqual(commonDir, worktreeGitDir)
                || !PathCasing.IsPathEqualOrParent(commonDir, worktreeGitDir))
                return null;
            return commonDir;
        }

        // Fallback only for a real git-dir shape (e.g. submodules), not any directory.
        // 任意のディレクトリではなく、実際の git-dir 形状（例: submodule）の場合だけフォールバックする。
        if (!IsValidGitDirectoryShape(worktreeGitDir))
            return null;
        return worktreeGitDir;
    }

    private static bool GitMetadataPathExists(string path)
        => FileSystemBoundary.TryGetAttributes(path, out _) != FileSystemBoundaryProbeStatus.Missing;

    private static bool TryResolveGitMetadataPath(string baseDirectory, string value, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            var fullPath = Path.IsPathRooted(value)
                ? Path.GetFullPath(value)
                : Path.GetFullPath(Path.Combine(baseDirectory, value));
            resolvedPath = PathCasing.NormalizeBoundaryPath(fullPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRegularGitMetadataFile(string path)
        => TryValidateGitMetadataEntry(path, expectDirectory: false, out _);

    private static bool TryValidateGitMetadataEntry(string path, bool expectDirectory, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        try
        {
            canonicalPath = PathCasing.NormalizeBoundaryPath(Path.GetFullPath(path));
            if (!TryValidateGitMetadataPathComponents(canonicalPath, expectDirectory))
                return false;

            if (!OperatingSystem.IsWindows())
            {
                if (!TrustedExecutableValidator.TryResolveRealUnixPath(canonicalPath, out var resolvedPath)
                    || !TryValidateGitMetadataPathComponents(resolvedPath, expectDirectory))
                {
                    return false;
                }
            }

            if (!expectDirectory
                && (!FileIndexer.TryGetFileLinkCount(LongPath.EnsureWindowsPrefix(canonicalPath), out var linkCount)
                    || linkCount != 1))
            {
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException or CodeIndexException)
        {
            canonicalPath = string.Empty;
            return false;
        }
    }

    private static bool TryValidateGitMetadataPathComponents(string fullPath, bool expectDirectory)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
            return false;

        var relativePath = fullPath[root.Length..];
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(current));
            if (FileSystemBoundary.IsDevice(attributes))
            {
                return false;
            }

            if (FileSystemBoundary.IsSymlinkOrReparsePoint(attributes))
            {
                if (index == segments.Length - 1 || !IsTrustedSystemMetadataLink(current))
                    return false;
                continue;
            }

            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if (index < segments.Length - 1 && !isDirectory)
                return false;
            if (index == segments.Length - 1 && isDirectory != expectDirectory)
                return false;
        }

        if (segments.Length > 0)
            return true;

        var rootAttributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(root));
        return !FileSystemBoundary.IsSymlinkOrReparsePoint(rootAttributes)
               && !FileSystemBoundary.IsDevice(rootAttributes)
               && ((rootAttributes & FileAttributes.Directory) != 0) == expectDirectory;
    }

    private static bool IsTrustedSystemMetadataLink(string path)
    {
        if (OperatingSystem.IsWindows())
            return false;

        var parent = Directory.GetParent(path)?.FullName;
        if (string.IsNullOrEmpty(parent)
            || !FileIndexer.TryGetUnixFileOwnerId(LongPath.EnsureWindowsPrefix(path), out var linkOwner)
            || linkOwner != 0
            || !FileIndexer.TryGetUnixFileOwnerId(LongPath.EnsureWindowsPrefix(parent), out var parentOwner)
            || parentOwner != 0)
        {
            return false;
        }

        var parentMode = File.GetUnixFileMode(LongPath.EnsureWindowsPrefix(parent));
        return (parentMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0;
    }

    internal static bool TryResolveGitMetadataChildPath(
        string parentDirectory,
        string childName,
        bool expectDirectory,
        bool allowMissing,
        out string childPath)
    {
        childPath = string.Empty;
        if (string.IsNullOrWhiteSpace(childName)
            || childName is "." or ".."
            || !string.Equals(Path.GetFileName(childName), childName, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            if (!TryValidateGitMetadataEntry(parentDirectory, expectDirectory: true, out var canonicalParent))
                return false;

            childPath = PathCasing.NormalizeBoundaryPath(Path.Combine(canonicalParent, childName));
            if (!FileSystemBoundary.IsStrictDescendant(canonicalParent, childPath))
            {
                childPath = string.Empty;
                return false;
            }

            var probe = FileSystemBoundary.TryGetAttributes(childPath, out _);
            if (probe == FileSystemBoundaryProbeStatus.Missing)
                return allowMissing;
            if (probe != FileSystemBoundaryProbeStatus.Found
                || !TryValidateGitMetadataEntry(childPath, expectDirectory, out childPath))
            {
                childPath = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException or CodeIndexException)
        {
            childPath = string.Empty;
            return false;
        }
    }

    private static bool IsValidGitDirectoryShape(string gitDir)
    {
        try
        {
            if (!TryValidateGitMetadataEntry(gitDir, expectDirectory: true, out gitDir))
                return false;

            var headPath = LongPath.EnsureWindowsPrefix(Path.Combine(gitDir, "HEAD"));
            if (!IsRegularGitMetadataFile(headPath))
                return false;

            return IsGitMetadataDirectory(Path.Combine(gitDir, "objects"))
                   && IsGitMetadataDirectory(Path.Combine(gitDir, "refs"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsGitMetadataDirectory(string path)
        => TryValidateGitMetadataEntry(path, expectDirectory: true, out _);

    /// <summary>
    /// Try to classify the repository shape for <paramref name="projectRoot"/>.
    /// projectRoot の git リポジトリ形状を best-effort で判定する。
    /// </summary>
    public static GitRepositoryType TryGetRepositoryType(string projectRoot, CancellationToken cancellationToken = default)
    {
        var dotGit = Path.Combine(projectRoot, ".git");
        var ioDotGit = LongPath.EnsureWindowsPrefix(dotGit);
        if (TryValidateGitMetadataEntry(dotGit, expectDirectory: true, out _))
            return GitRepositoryType.Normal;
        if (TryValidateGitMetadataEntry(dotGit, expectDirectory: false, out _))
            return GitRepositoryType.Worktree;
        if (GitMetadataPathExists(ioDotGit))
            return GitRepositoryType.None;

        var isBare = TryRunGit(projectRoot, cancellationToken, "rev-parse", "--is-bare-repository")?.Trim();
        return string.Equals(isBare, "true", StringComparison.OrdinalIgnoreCase)
            ? GitRepositoryType.Bare
            : GitRepositoryType.None;
    }

    /// <summary>
    /// Get changed files from a git commit.
    /// gitコミットから変更ファイルを取得する。
    /// </summary>
    public static List<string> GetChangedFilesFromCommit(
        string projectRoot,
        string commitId,
        CancellationToken cancellationToken = default)
    {
        ValidateSingleCommitRef(projectRoot, commitId, cancellationToken);

        var psi = CreateGitStartInfoOrThrow(projectRoot);
        psi.ArgumentList.Add("diff-tree");
        psi.ArgumentList.Add("--no-commit-id");
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("-M");
        psi.ArgumentList.Add("--name-status");
        psi.ArgumentList.Add("-z");
        psi.ArgumentList.Add(commitId);

        var (exitCode, output, error) = RunProcessCapturingOutput(psi, cancellationToken)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");

        if (exitCode != 0)
            throw new InvalidOperationException($"git diff-tree failed for commit {commitId}: {error.Trim()}");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        AddNameStatusPaths(output, paths);

        return paths.ToList();
    }

    public static bool IsCommitObjectId(string value)
        => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9a-fA-F]{7,40}$");

    public static void ValidateCommitRef(string projectRoot, string commitRef)
        => ValidateSingleCommitRef(projectRoot, commitRef);

    private static void ValidateSingleCommitRef(
        string projectRoot,
        string commitId,
        CancellationToken cancellationToken = default)
    {
        if (TryResolveGitExecutablePath() == null)
            throw new InvalidOperationException(ValidatedGitUnavailableMessage);

        // Reject range/pathspec syntax before invoking git so --commits remains a list
        // of single commit-ish values, not revision-set expressions.
        if (string.IsNullOrWhiteSpace(commitId)
            || commitId.StartsWith('-')
            || commitId.Contains("..", StringComparison.Ordinal)
            || commitId.Contains("^{", StringComparison.Ordinal)
            || commitId.Contains(':')
            || !Regex.IsMatch(commitId, @"^[a-zA-Z0-9_./^~\-]+$"))
        {
            throw new ArgumentException(
                $"Invalid commit ID '{commitId}'. Provide a single commit-ish; ranges and tag refs are not accepted. Use `git rev-parse --verify <ref>^{{commit}}` to validate it.");
        }

        var symbolicName = TryRunGit(projectRoot, cancellationToken, "rev-parse", "--symbolic-full-name", commitId)?.Trim();
        if (symbolicName != null && symbolicName.StartsWith("refs/tags/", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Invalid commit ID '{commitId}'. Tag refs are not accepted for --commits; pass the peeled commit SHA from `git rev-parse --verify {commitId}^{{commit}}`.");
        }

        var resolved = TryRunGit(projectRoot, cancellationToken, "rev-parse", "--verify", $"{commitId}^{{commit}}")?.Trim();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new ArgumentException(
                $"Invalid commit ID '{commitId}'. Git could not resolve it to a single commit. Use `git rev-parse --verify <ref>^{{commit}}` to validate it.");
        }
    }

    /// <summary>
    /// Get changed files between two git refs, including both sides of renames.
    /// 2つのgit ref間の変更ファイルを取得する。rename は旧パスと新パスの両方を含める。
    /// </summary>
    public static List<string> GetChangedFilesBetweenRefs(
        string projectRoot,
        string oldRef,
        string newRef,
        CancellationToken cancellationToken = default)
    {
        ValidateGitRef(oldRef, nameof(oldRef));
        ValidateGitRef(newRef, nameof(newRef));

        var psi = CreateGitStartInfoOrThrow(projectRoot);
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add("--name-status");
        psi.ArgumentList.Add("-z");
        psi.ArgumentList.Add("-M");
        psi.ArgumentList.Add(oldRef);
        psi.ArgumentList.Add(newRef);
        psi.ArgumentList.Add("--");

        var (exitCode, output, error) = RunProcessCapturingOutput(psi, cancellationToken)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");

        if (exitCode != 0)
            throw new InvalidOperationException($"git diff failed between {oldRef} and {newRef}: {error.Trim()}");

        var paths = new List<string>();
        AddNameStatusPaths(output, paths);

        return paths;
    }

    private static void AddNameStatusPaths(string output, ICollection<string> paths)
    {
        if (output.IndexOf('\0') >= 0)
        {
            AddNullDelimitedNameStatusPaths(output, paths);
            return;
        }

        var lineStart = 0;
        while (lineStart < output.Length)
        {
            var newlineIndex = output.IndexOf('\n', lineStart);
            var lineEnd = newlineIndex < 0 ? output.Length : newlineIndex;
            var line = output.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r');
            if (!line.IsEmpty)
                AddNameStatusLinePaths(line, paths);

            if (newlineIndex < 0)
                break;

            lineStart = newlineIndex + 1;
        }
    }

    private static void AddNullDelimitedNameStatusPaths(
        string output,
        ICollection<string> paths)
    {
        var offset = 0;
        while (TryReadNullDelimitedField(output, ref offset, out var status))
        {
            if (status.IsEmpty
                || !TryReadNullDelimitedField(output, ref offset, out var firstPath))
            {
                continue;
            }

            if ((status[0] is 'R' or 'C')
                && TryReadNullDelimitedField(output, ref offset, out var secondPath))
            {
                paths.Add(FileIndexer.NormalizePathSeparators(firstPath.ToString()));
                paths.Add(FileIndexer.NormalizePathSeparators(secondPath.ToString()));
            }
            else
            {
                paths.Add(FileIndexer.NormalizePathSeparators(firstPath.ToString()));
            }
        }
    }

    private static bool TryReadNullDelimitedField(
        string output,
        ref int offset,
        out ReadOnlySpan<char> field)
    {
        field = default;
        if (offset >= output.Length)
            return false;

        var remaining = output.AsSpan(offset);
        var terminator = remaining.IndexOf('\0');
        if (terminator < 0)
        {
            field = remaining;
            offset = output.Length;
            return true;
        }

        field = remaining[..terminator];
        offset += terminator + 1;
        return true;
    }

    private static void AddNameStatusLinePaths(ReadOnlySpan<char> line, ICollection<string> paths)
    {
        ReadOnlySpan<char> status = default;
        ReadOnlySpan<char> firstPath = default;
        ReadOnlySpan<char> secondPath = default;
        var fieldIndex = 0;
        var fieldStart = 0;
        while (fieldStart <= line.Length && fieldIndex < 3)
        {
            var nextTab = line[fieldStart..].IndexOf('\t');
            var field = nextTab < 0
                ? line[fieldStart..]
                : line.Slice(fieldStart, nextTab);
            if (!field.IsEmpty)
            {
                switch (fieldIndex)
                {
                    case 0:
                        status = field;
                        break;
                    case 1:
                        firstPath = field;
                        break;
                    case 2:
                        secondPath = field;
                        break;
                }

                fieldIndex++;
            }

            if (nextTab < 0)
                break;

            fieldStart += nextTab + 1;
        }

        if (status.IsEmpty)
            return;

        if ((status[0] is 'R' or 'C') && !firstPath.IsEmpty && !secondPath.IsEmpty)
        {
            paths.Add(FileIndexer.NormalizePathSeparators(firstPath.ToString()));
            paths.Add(FileIndexer.NormalizePathSeparators(secondPath.ToString()));
        }
        else if (!firstPath.IsEmpty)
        {
            paths.Add(FileIndexer.NormalizePathSeparators(firstPath.ToString()));
        }
    }

    private static void ValidateGitRef(string value, string parameterName)
    {
        // Reject values starting with "-" to prevent git option injection even though
        // callers also add "--" after the refs.
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || !Regex.IsMatch(value, @"^[a-zA-Z0-9_./^~:@{}-]+$"))
            throw new ArgumentException($"Invalid git ref: {value}", parameterName);
    }

    /// <summary>
    /// Try to resolve the current HEAD commit for the repository that contains the project root.
    /// projectRoot を含むリポジトリの現在の HEAD コミットを安全に取得する。
    /// </summary>
    public static string? TryGetHeadCommit(string projectRoot, CancellationToken cancellationToken = default)
    {
        var result = TryGetHeadCommitResult(projectRoot, cancellationToken);
        return result.State is GitHeadCommitState.Resolved or GitHeadCommitState.DetachedHead
            ? result.Sha
            : null;
    }

    /// <summary>
    /// Try to resolve a git ref to a commit SHA. Pass a caller token for cancelable production paths;
    /// the default token preserves legacy best-effort behavior for compatibility.
    /// git ref を commit SHA に解決する。production 経路では caller token を渡す。
    /// </summary>
    public static string? TryResolveCommit(string projectRoot, string refName, CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateGitRef(refName, nameof(refName));
            return TryRunGit(projectRoot, cancellationToken, "rev-parse", "--verify", $"{refName}^{{commit}}")?.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static GitHeadCommitResult TryGetHeadCommitResult(string projectRoot, CancellationToken cancellationToken = default)
        => TryGetHeadCommitResult(projectRoot, gitEnvironmentOverrides: null, cancellationToken);

    internal static GitHeadCommitResult TryGetHeadCommitResult(
        string projectRoot,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken = default)
    {
        var repositoryRoot = TryGetRepositoryRootResult(projectRoot, gitEnvironmentOverrides, cancellationToken);
        if (repositoryRoot.FailureKind != GitCommandFailureKind.None)
        {
            var diagnostic = repositoryRoot.Diagnostic ?? "git could not resolve the repository root";
            return GitHeadCommitResult.Error(diagnostic, repositoryRoot.FailureKind, diagnostic);
        }

        if (repositoryRoot.Root == null)
        {
            return HasGitMetadataEntry(projectRoot)
                ? GitHeadCommitResult.Error("git repository metadata is present, but git could not resolve the repository root")
                : GitHeadCommitResult.NotARepo;
        }

        var headResult = RunGitCapturingResult(projectRoot, gitEnvironmentOverrides, cancellationToken, "rev-parse", "--verify", "HEAD^{commit}");
        if (headResult.StartError != null)
            return GitHeadCommitResult.Error(headResult.StartError, headResult.FailureKind, headResult.Diagnostic);

        var sha = headResult.Output?.Trim();
        if (headResult.ExitCode != 0)
        {
            var reason = NormalizeGitError(headResult.Error);
            return IsMissingHeadError(reason)
                ? GitHeadCommitResult.None
                : GitHeadCommitResult.Error(reason, headResult.FailureKind, headResult.Diagnostic);
        }

        if (string.IsNullOrWhiteSpace(sha))
            return GitHeadCommitResult.None;

        var branchResult = RunGitCapturingResult(projectRoot, gitEnvironmentOverrides, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");
        if (branchResult.StartError != null)
            return GitHeadCommitResult.Error(branchResult.StartError, branchResult.FailureKind, branchResult.Diagnostic);
        if (branchResult.ExitCode != 0)
            return GitHeadCommitResult.Error(
                NormalizeGitError(branchResult.Error),
                branchResult.FailureKind,
                branchResult.Diagnostic);

        var branch = branchResult.Output?.Trim();
        return string.Equals(branch, "HEAD", StringComparison.Ordinal)
            ? GitHeadCommitResult.DetachedHead(sha)
            : GitHeadCommitResult.Resolved(sha);
    }

    /// <summary>
    /// Try to resolve the current branch short name. Returns null on detached HEAD
    /// (`git rev-parse --abbrev-ref HEAD` prints `HEAD` in that state, which we treat
    /// as "no branch" so callers can render it as detached without misclassifying it
    /// as the literal branch name "HEAD"). Issue #1509.
    /// 現在のブランチ短縮名を安全に取得する。detached HEAD は null 扱いにして、
    /// 文字列 "HEAD" を誤ってブランチ名として永続化しないようにする。
    /// </summary>
    public static string? TryGetHeadBranch(string projectRoot, CancellationToken cancellationToken = default)
    {
        var output = TryRunGit(projectRoot, cancellationToken, "rev-parse", "--abbrev-ref", "HEAD");
        var value = output?.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return string.Equals(value, "HEAD", StringComparison.Ordinal) ? null : value;
    }

    /// <summary>
    /// Try to count how many commits the current HEAD is ahead of <paramref name="baseCommit"/>.
    /// Returns null when git is unavailable, when either side cannot be resolved, or when
    /// the two commits are not on a linear ancestor relationship (e.g. force-push rewrite).
    /// 0 means "indexed HEAD equals current HEAD". A positive number means current HEAD is
    /// N commits ahead of the indexed commit. Issue #1509.
    /// 現在の HEAD が指定 commit より何コミット進んでいるかを安全に数える。git が無い、
    /// commit が解決できない、または線形な祖先関係に無い場合は null を返す。
    /// </summary>
    public static int? TryCountCommitsAhead(
        string projectRoot,
        string baseCommit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseCommit))
            return null;
        if (baseCommit.StartsWith('-') || !Regex.IsMatch(baseCommit, @"^[a-zA-Z0-9_./^~\-]+$"))
            return null;

        var headSha = TryGetHeadCommit(projectRoot, cancellationToken);
        if (string.IsNullOrWhiteSpace(headSha))
            return null;

        // Identical commit short-circuit: rev-list would print 0, but avoid spawning git
        // for the common "index is current" path.
        // 同一 commit のショートカット。よくある「index が最新」パスで git 起動を避ける。
        if (string.Equals(headSha, baseCommit, StringComparison.OrdinalIgnoreCase))
            return 0;

        // Require the indexed commit to be an ancestor of HEAD. Otherwise "ahead by N"
        // is misleading (history rewrite, divergent branch, indexed commit was a future
        // branch tip, etc.). rev-list will succeed with exit=0 but a misleading count.
        // indexed commit が現在 HEAD の祖先である場合のみ「N コミット進んでいる」の解釈が
        // 成立するので、merge-base --is-ancestor で検証する。
        if (!TryRunGitForExitCode(projectRoot, cancellationToken, "merge-base", "--is-ancestor", baseCommit, "HEAD"))
            return null;

        var output = TryRunGit(projectRoot, cancellationToken, "rev-list", "--count", $"{baseCommit}..HEAD");
        if (output == null)
            return null;
        var trimmed = output.Trim();
        return int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
    }

    private static bool TryRunGitForExitCode(string projectRoot, CancellationToken cancellationToken, params string[] args)
    {
        try
        {
            var psi = TryCreateGitStartInfo(projectRoot);
            if (psi == null)
                return false;

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            // Reuse the shared event-driven drainer (PR #1497) so we don't reintroduce
            // sync-over-async on git's stderr pipe. We only care about exit code here.
            // #1497 で導入した共有 drainer を使い、stderr の sync-over-async を再導入しない。
            var result = RunProcessCapturingOutput(psi, cancellationToken);
            return result != null && result.Value.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Try to resolve the repository root that contains the project path.
    /// projectPath を含むリポジトリのルートを安全に取得する。
    /// </summary>
    public static string? TryGetRepositoryRoot(string projectPath, CancellationToken cancellationToken = default)
        => TryGetRepositoryRoot(projectPath, gitEnvironmentOverrides: null, cancellationToken);

    internal static GitRepositoryType TryGetRepositoryType(
        string projectRoot,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken = default)
    {
        var dotGit = Path.Combine(projectRoot, ".git");
        var ioDotGit = LongPath.EnsureWindowsPrefix(dotGit);
        if (Directory.Exists(ioDotGit))
            return GitRepositoryType.Normal;
        if (File.Exists(ioDotGit))
            return GitRepositoryType.Worktree;

        var isBare = TryRunGit(projectRoot, gitEnvironmentOverrides, cancellationToken, "rev-parse", "--is-bare-repository")?.Trim();
        return string.Equals(isBare, "true", StringComparison.OrdinalIgnoreCase)
            ? GitRepositoryType.Bare
            : GitRepositoryType.None;
    }

    /// <summary>
    /// Resolve whether ignore matching should be case-insensitive for this workspace.
    /// git 管理下なら core.ignorecase を優先し、そうでなければファイルシステム特性を推定する。
    /// </summary>
    public static bool ResolveIgnoreCase(string projectRoot, CancellationToken cancellationToken = default)
        => ResolveIgnoreCase(projectRoot, gitEnvironmentOverrides: null, cancellationToken);

    internal static bool ResolveIgnoreCase(
        string projectRoot,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken = default)
    {
        var repoRoot = TryGetRepositoryRoot(projectRoot, gitEnvironmentOverrides, cancellationToken);
        if (repoRoot == null)
            return ProbeFileSystemIgnoreCase(projectRoot, cancellationToken);

        var configured = TryRunGit(repoRoot, gitEnvironmentOverrides, cancellationToken, "config", "--bool", "--get", "core.ignorecase")?.Trim();
        if (bool.TryParse(configured, out var ignoreCase))
            return ignoreCase;

        return ProbeFileSystemIgnoreCase(projectRoot, cancellationToken);
    }

    /// <summary>
    /// Try to determine whether the worktree has uncommitted changes.
    /// worktree に未コミット変更があるか安全に判定する。
    /// </summary>
    public static bool? TryIsWorktreeDirty(string projectRoot, CancellationToken cancellationToken = default)
    {
        var status = TryGetWorktreeStatus(projectRoot, cancellationToken);
        return status?.IsDirty;
    }

    /// <summary>
    /// Try to determine worktree dirtiness and unresolved merge paths from git porcelain status.
    /// git porcelain status から worktree の dirty 状態と未解決 merge path を取得する。
    /// </summary>
    public static WorktreeStatus? TryGetWorktreeStatus(string projectRoot, CancellationToken cancellationToken = default)
    {
        var output = TryRunGit(
            projectRoot,
            cancellationToken,
            "-c",
            "core.quotePath=false",
            "status",
            "--porcelain",
            "--untracked-files=all");
        if (output == null)
            return null;

        var unresolved = new List<string>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 3)
                continue;

            var status = line[..2];
            if (!UnresolvedMergeStatuses.Contains(status))
                continue;

            unresolved.Add(ParsePorcelainPath(line[3..]));
        }

        return new WorktreeStatus(output.Length > 0, unresolved);
    }

    private static string ParsePorcelainPath(string path)
    {
        var renameSeparator = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (renameSeparator >= 0)
            path = path[(renameSeparator + 4)..];
        return FileIndexer.NormalizePathSeparators(path);
    }

    /// <summary>
    /// Return the set of tracked paths whose skip-worktree bit is set, scoped to
    /// the directory the call was made from. Paths are forward-slash separated and
    /// relative to <paramref name="projectRoot"/>, matching DB / scan-result path form.
    /// Returns null when git is unavailable; returns an empty set when no entry is
    /// flagged. Skip-worktree is the mechanism git uses for sparse-checkout (cone or
    /// non-cone), partial clones, and manual <c>git update-index --skip-worktree</c>.
    /// projectRoot 配下の git index で skip-worktree ビットを持つトラッキング対象パスを返す。
    /// 区切り文字は forward slash、projectRoot からの相対表現で DB と揃える。
    /// git が無い場合は null、該当無しは空集合を返す。sparse-checkout(cone/non-cone)・partial
    /// clone・手動 update-index --skip-worktree がいずれも同じビットを使うのを横断的に拾う。
    /// </summary>
    public static HashSet<string>? TryGetSkipWorktreePaths(string projectRoot, CancellationToken cancellationToken = default)
        => TryGetSkipWorktreePaths(projectRoot, gitEnvironmentOverrides: null, cancellationToken);

    internal static HashSet<string>? TryGetSkipWorktreePaths(
        string projectRoot,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken = default)
    {
        var output = TryRunGit(
            projectRoot,
            gitEnvironmentOverrides,
            cancellationToken,
            "-c",
            "core.quotePath=false",
            "ls-files",
            "-t");
        if (output == null)
            return null;

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            // Format: "<flag> <path>". 'S' (uppercase) marks skip-worktree.
            // 形式: "<flag> <path>"。'S'(大文字) が skip-worktree を表す。
            if (line.Length < 3 || line[1] != ' ' || line[0] != 'S')
                continue;
            paths.Add(FileIndexer.NormalizePathSeparators(line[2..]));
        }
        return paths;
    }

    /// <summary>
    /// Return whether tracked index flags can hide worktree changes from ordinary Git status.
    /// Null means Git could not provide a trustworthy answer. Both skip-worktree and
    /// assume-unchanged are visibility-limiting for freshness purposes (#5227).
    /// 通常の Git status から worktree 変更を隠し得る tracked index flag の有無を返す。
    /// Git で確認できない場合は null。freshness 判定では skip-worktree と
    /// assume-unchanged の両方を visibility 制限として扱う (#5227)。
    /// </summary>
    internal static bool? TryHasWorktreeVisibilityLimitingIndexFlags(
        string projectRoot,
        CancellationToken cancellationToken = default)
    {
        var output = TryRunGit(
            projectRoot,
            gitEnvironmentOverrides: null,
            cancellationToken,
            "-c",
            "core.quotePath=false",
            "ls-files",
            "-v");
        if (output == null)
            return null;

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 3 || line[1] != ' ')
                continue;

            // `S` is skip-worktree. With `-v`, any lowercase tag means the
            // assume-unchanged bit is set (including lowercase `s`).
            // `S` は skip-worktree。`-v` の小文字 tag は assume-unchanged を示す。
            if (line[0] == 'S' || char.IsLower(line[0]))
                return true;
        }

        return false;
    }

    internal static string? TryGetRepositoryRoot(
        string projectPath,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken = default)
        => TryGetRepositoryRootResult(projectPath, gitEnvironmentOverrides, cancellationToken).Root;

    private static GitRepositoryRootResult TryGetRepositoryRootResult(
        string projectPath,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken = default)
    {
        var cdup = RunGitCapturingResult(projectPath, gitEnvironmentOverrides, cancellationToken, "rev-parse", "--show-cdup");
        if (cdup.FailureKind == GitCommandFailureKind.None)
        {
            var value = cdup.Output?.Trim() ?? string.Empty;
            var root = string.IsNullOrEmpty(value)
                ? Path.GetFullPath(projectPath)
                : Path.GetFullPath(Path.Combine(projectPath, value));
            return GitRepositoryRootResult.Resolved(root);
        }
        if (IsGitInfrastructureFailure(cdup.FailureKind))
            return GitRepositoryRootResult.Failure(cdup.FailureKind, cdup.Diagnostic);

        var isBare = RunGitCapturingResult(projectPath, gitEnvironmentOverrides, cancellationToken, "rev-parse", "--is-bare-repository");
        if (isBare.FailureKind == GitCommandFailureKind.None
            && string.Equals(isBare.Output?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return GitRepositoryRootResult.Resolved(Path.GetFullPath(projectPath));
        }
        if (IsGitInfrastructureFailure(isBare.FailureKind))
            return GitRepositoryRootResult.Failure(isBare.FailureKind, isBare.Diagnostic);

        return GitRepositoryRootResult.NotARepo;
    }

    private static bool IsGitInfrastructureFailure(GitCommandFailureKind failureKind)
        => failureKind is not GitCommandFailureKind.None and not GitCommandFailureKind.ExitCode;

    private static bool HasGitMetadataEntry(string projectRoot)
    {
        var dotGit = Path.Combine(projectRoot, ".git");
        var ioDotGit = LongPath.EnsureWindowsPrefix(dotGit);
        return Directory.Exists(ioDotGit) || File.Exists(ioDotGit);
    }

    private static string? TryRunGit(string projectRoot, CancellationToken cancellationToken, params string[] args)
        => TryRunGit(projectRoot, gitEnvironmentOverrides: null, cancellationToken, args);

    internal readonly record struct GitCommandResult(
        int? ExitCode,
        string? Output,
        string? Error,
        GitCommandFailureKind FailureKind,
        string? Diagnostic)
    {
        public string? StartError
            => FailureKind is GitCommandFailureKind.TrustedGitUnavailable
                or GitCommandFailureKind.StartFailed
                or GitCommandFailureKind.Exception
                ? Diagnostic
                : null;
    }

    private readonly record struct GitRepositoryRootResult(
        string? Root,
        GitCommandFailureKind FailureKind,
        string? Diagnostic)
    {
        public static GitRepositoryRootResult NotARepo { get; } = new(null, GitCommandFailureKind.None, null);
        public static GitRepositoryRootResult Resolved(string root) => new(root, GitCommandFailureKind.None, null);
        public static GitRepositoryRootResult Failure(GitCommandFailureKind failureKind, string? diagnostic)
            => new(null, failureKind, diagnostic);
    }

    private static string? TryRunGit(
        string projectRoot,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken,
        params string[] args)
    {
        try
        {
            var psi = TryCreateGitStartInfo(projectRoot);
            if (psi == null)
                return null;

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            if (gitEnvironmentOverrides != null)
            {
                foreach (var (key, value) in gitEnvironmentOverrides)
                {
                    if (value == null)
                        psi.Environment.Remove(key);
                    else
                        psi.Environment[key] = value;
                }
            }

            var result = RunProcessCapturingOutput(psi, cancellationToken);
            if (result == null)
                return null;

            var (exitCode, output, _) = result.Value;
            return exitCode == 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static GitCommandResult RunGitCapturingResult(
        string projectRoot,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken = default,
        params string[] args)
    {
        try
        {
            var psi = TryCreateGitStartInfo(projectRoot);
            if (psi == null)
            {
                var diagnostic = FormatGitDiagnostic(ValidatedGitUnavailableMessage);
                return new GitCommandResult(
                    null,
                    null,
                    diagnostic,
                    GitCommandFailureKind.TrustedGitUnavailable,
                    diagnostic);
            }

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            if (gitEnvironmentOverrides != null)
            {
                foreach (var (key, value) in gitEnvironmentOverrides)
                {
                    if (value == null)
                        psi.Environment.Remove(key);
                    else
                        psi.Environment[key] = value;
                }
            }

            var result = RunProcessCapturingResult(psi, cancellationToken);
            return result == null
                ? new GitCommandResult(null, null, null, GitCommandFailureKind.StartFailed, "Failed to start git process / gitプロセスの起動に失敗")
                : new GitCommandResult(
                    result.Value.ExitCode,
                    result.Value.Output,
                    result.Value.Error,
                    result.Value.FailureKind,
                    result.Value.Diagnostic);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var diagnostic = FormatGitDiagnostic($"git helper exception: {DiagnosticRedactor.ClassifyException(ex)}: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}");
            return new GitCommandResult(null, null, diagnostic, GitCommandFailureKind.Exception, diagnostic);
        }
    }

    internal static GitCommandResult RunGitCapturingResultForTests(
        string projectRoot,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides,
        CancellationToken cancellationToken = default,
        params string[] args)
        => RunGitCapturingResult(projectRoot, gitEnvironmentOverrides, cancellationToken, args);

    private static string NormalizeGitError(string? error)
    {
        var reason = error?.Trim();
        return string.IsNullOrWhiteSpace(reason) ? "git command failed without stderr" : reason;
    }

    private static bool IsMissingHeadError(string reason)
        => reason.Contains("Needed a single revision", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("ambiguous argument 'HEAD", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("bad revision 'HEAD", StringComparison.OrdinalIgnoreCase);

    private static (int ExitCode, string Output, string Error)? RunProcessCapturingOutput(
        ProcessStartInfo psi,
        CancellationToken cancellationToken = default)
        => GitProcessRunner.RunCapturingOutput(psi, GitCommandTimeout, cancellationToken);

    private static GitProcessRunner.CaptureResult? RunProcessCapturingResult(
        ProcessStartInfo psi,
        CancellationToken cancellationToken = default)
        => GitProcessRunner.RunCapturingResult(psi, GitCommandTimeout, cancellationToken);

    private static string FormatGitDiagnostic(string diagnostic) =>
        GitProcessRunner.FormatDiagnostic(diagnostic);

    private static bool ProbeFileSystemIgnoreCase(
        string projectRoot,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = projectRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(projectRoot);
            if (FileSystemIgnoreCaseProbeForTesting is { } probeOverride)
                return probeOverride(normalizedRoot);

            if (Directory.Exists(LongPath.EnsureWindowsPrefix(normalizedRoot)))
            {
                try
                {
                    if (CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(normalizedRoot, cancellationToken) is { } existingChildIgnoreCase)
                        return existingChildIgnoreCase;
                }
                catch (Exception ex) when (IsCaseSensitivityProbeFailure(ex))
                {
                    // An unreadable workspace cannot reveal the policy of names inside it.
                    // Use the containing namespace so discovery can report the traversal
                    // failure itself instead of failing during preliminary case probing.
                    // unreadable workspace 内の名前 policy は probe できないため、包含する
                    // namespace の policy を使い、事前 case probe ではなく traversal 本体で
                    // failure を報告できるようにする。
                    var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(normalizedRoot));
                    if (!string.IsNullOrEmpty(parent)
                        && !string.Equals(parent, normalizedRoot, StringComparison.Ordinal))
                    {
                        return ProbeFileSystemIgnoreCase(parent, cancellationToken);
                    }

                    throw;
                }
            }

            return CaseSensitivityProbeDirectory.ProbeIgnoreCase(normalizedRoot, "case-probe-", cancellationToken);
        }
        catch (CaseSensitivityProbeException ex)
        {
            throw CreateCaseSensitivityProbeException(normalizedRoot, ex);
        }
        catch (Exception ex) when (IsCaseSensitivityProbeFailure(ex))
        {
            throw CreateCaseSensitivityProbeException(normalizedRoot, ex);
        }
    }

    private static CodeIndexException CreateCaseSensitivityProbeException(string projectRoot, Exception innerException)
        => new(
            code: CommandErrorCodes.FileSystemCaseProbeFailed,
            category: CodeIndexExceptionCategory.Filesystem,
            message: "Failed to determine filesystem case sensitivity.",
            path: TryNormalizePathForError(projectRoot),
            hint: "Ensure the workspace and its .cdidx probe directory are readable and writable, then rerun the command.",
            innerException: innerException);

    private static string TryNormalizePathForError(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (IsCaseSensitivityProbeFailure(ex))
        {
            return path;
        }
    }

    private static bool IsCaseSensitivityProbeFailure(Exception ex)
        => ex is ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException;

}
