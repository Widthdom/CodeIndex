using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;

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
public static class GitHelper
{
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

    internal const int MaxCapturedGitOutputChars = 1024 * 1024;
    internal const int MaxGitFailureDiagnosticChars = 512;
    private const int GitCaptureReadBufferChars = 8192;
    private static readonly TimeSpan DefaultGitCommandTimeout = TimeSpan.FromSeconds(60);
    private static readonly AsyncLocal<TimeSpan?> GitCommandTimeoutOverride = new();
    internal static TimeSpan GitCommandTimeout
    {
        get => GitCommandTimeoutOverride.Value ?? DefaultGitCommandTimeout;
        set => GitCommandTimeoutOverride.Value = value;
    }

    private static readonly Lazy<string?> TrustedGitExecutablePath = new(ResolveTrustedGitExecutablePathFromKnownLocations);
    private static readonly AsyncLocal<string?> GitExecutablePathOverrideValue = new();
    internal static string? GitExecutablePathOverride
    {
        get => GitExecutablePathOverrideValue.Value;
        set => GitExecutablePathOverrideValue.Value = value;
    }

    private static readonly TimeSpan GitKillWaitTimeout = TimeSpan.FromSeconds(5);
    private const int GitProcessFailureExitCode = -1;
    private const string TrustedGitUnavailableMessage =
        "Could not resolve a trusted git executable path. Install git in a standard system location. / 信頼済みの git 実行ファイルパスを解決できませんでした。標準のシステム場所に git をインストールしてください。";

    private static ProcessStartInfo? TryCreateGitStartInfo(string projectRoot)
    {
        var gitExecutablePath = TryResolveGitExecutablePath();
        if (gitExecutablePath == null)
            return null;

        return new ProcessStartInfo
        {
            FileName = gitExecutablePath,
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static ProcessStartInfo CreateGitStartInfoOrThrow(string projectRoot)
        => TryCreateGitStartInfo(projectRoot) ?? throw new InvalidOperationException(TrustedGitUnavailableMessage);

    private static string? TryResolveGitExecutablePath()
    {
        var overridePath = NormalizeTrustedGitExecutablePath(GitExecutablePathOverrideValue.Value);
        return overridePath ?? TrustedGitExecutablePath.Value;
    }

    private static string? ResolveTrustedGitExecutablePathFromKnownLocations()
    {
        foreach (var candidate in EnumerateTrustedGitExecutableCandidates())
        {
            var normalized = NormalizeTrustedGitExecutablePath(candidate);
            if (normalized != null)
                return normalized;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateTrustedGitExecutableCandidates()
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

    internal static IReadOnlyList<string> TrustedGitExecutableCandidatePathsForTests()
        => EnumerateTrustedGitExecutableCandidates().ToList();

    private static string? NormalizeTrustedGitExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            if (!Path.IsPathFullyQualified(path))
                return null;

            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileNameWithoutExtension(fullPath), "git", StringComparison.OrdinalIgnoreCase))
                return null;

            return File.Exists(LongPath.EnsureWindowsPrefix(fullPath)) ? fullPath : null;
        }
        catch
        {
            return null;
        }
    }

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
        if (Directory.Exists(ioDotGit)) return dotGit;

        // Worktree: .git is a file containing "gitdir: <path>" / worktree: .gitがファイルで "gitdir: <path>" を含む
        if (!File.Exists(ioDotGit))
        {
            return TryGetRepositoryType(projectRoot, cancellationToken) == GitRepositoryType.Bare
                ? Path.GetFullPath(projectRoot)
                : null;
        }

        if (!IsRegularGitMetadataFile(ioDotGit))
            return null;

        var gitFileContent = DataDirectorySecurity.ReadTextWithinLimit(ioDotGit, MaxGitMetadataFileBytes);
        if (gitFileContent is null)
            return null;

        gitFileContent = gitFileContent.Trim();
        if (!gitFileContent.StartsWith("gitdir:")) return null;

        var worktreeGitDirValue = gitFileContent["gitdir:".Length..].Trim();
        if (!TryResolveGitMetadataPath(projectRoot, worktreeGitDirValue, out var worktreeGitDir))
            return null;
        if (!Directory.Exists(LongPath.EnsureWindowsPrefix(worktreeGitDir)))
            return null;

        // Read commondir to find the shared .git directory / commondirを読んで共有.gitディレクトリを見つける
        var commonDirFile = Path.Combine(worktreeGitDir, "commondir");
        var ioCommonDirFile = LongPath.EnsureWindowsPrefix(commonDirFile);
        if (GitMetadataPathExists(ioCommonDirFile))
        {
            if (!IsRegularGitMetadataFile(ioCommonDirFile))
                return null;

            var commonDirRelative = DataDirectorySecurity.ReadTextWithinLimit(ioCommonDirFile, MaxGitMetadataFileBytes);
            if (commonDirRelative is null)
                return null;

            commonDirRelative = commonDirRelative.Trim();
            if (!TryResolveGitMetadataPath(worktreeGitDir, commonDirRelative, out var commonDir))
                return null;
            if (!Directory.Exists(LongPath.EnsureWindowsPrefix(commonDir)))
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
        => File.Exists(path) || Directory.Exists(path);

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
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) == 0
                   && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsValidGitDirectoryShape(string gitDir)
    {
        try
        {
            var ioGitDir = LongPath.EnsureWindowsPrefix(gitDir);
            if (!Directory.Exists(ioGitDir))
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
    {
        try
        {
            var attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(path));
            return (attributes & FileAttributes.Directory) != 0
                   && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Try to classify the repository shape for <paramref name="projectRoot"/>.
    /// projectRoot の git リポジトリ形状を best-effort で判定する。
    /// </summary>
    public static GitRepositoryType TryGetRepositoryType(string projectRoot, CancellationToken cancellationToken = default)
    {
        var dotGit = Path.Combine(projectRoot, ".git");
        var ioDotGit = LongPath.EnsureWindowsPrefix(dotGit);
        if (Directory.Exists(ioDotGit))
            return GitRepositoryType.Normal;
        if (File.Exists(ioDotGit))
            return GitRepositoryType.Worktree;

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
        psi.ArgumentList.Add(commitId);

        var (exitCode, output, error) = RunProcessCapturingOutput(psi, cancellationToken)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");

        if (exitCode != 0)
            throw new InvalidOperationException($"git diff-tree failed for commit {commitId}: {error.Trim()}");

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            var status = parts[0];
            if ((status.StartsWith('R') || status.StartsWith('C')) && parts.Length >= 3)
            {
                paths.Add(FileIndexer.NormalizePathSeparators(parts[1]));
                paths.Add(FileIndexer.NormalizePathSeparators(parts[2]));
            }
            else if (parts.Length >= 2)
            {
                paths.Add(FileIndexer.NormalizePathSeparators(parts[1]));
            }
        }

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
            throw new InvalidOperationException(TrustedGitUnavailableMessage);

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
        psi.ArgumentList.Add("-M");
        psi.ArgumentList.Add(oldRef);
        psi.ArgumentList.Add(newRef);
        psi.ArgumentList.Add("--");

        var (exitCode, output, error) = RunProcessCapturingOutput(psi, cancellationToken)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");

        if (exitCode != 0)
            throw new InvalidOperationException($"git diff failed between {oldRef} and {newRef}: {error.Trim()}");

        var paths = new List<string>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;

            var status = parts[0];
            if ((status.StartsWith('R') || status.StartsWith('C')) && parts.Length >= 3)
            {
                paths.Add(FileIndexer.NormalizePathSeparators(parts[1]));
                paths.Add(FileIndexer.NormalizePathSeparators(parts[2]));
            }
            else if (parts.Length >= 2)
            {
                paths.Add(FileIndexer.NormalizePathSeparators(parts[1]));
            }
        }

        return paths;
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
            return ProbeFileSystemIgnoreCase(projectRoot);

        var configured = TryRunGit(repoRoot, gitEnvironmentOverrides, cancellationToken, "config", "--bool", "--get", "core.ignorecase")?.Trim();
        if (bool.TryParse(configured, out var ignoreCase))
            return ignoreCase;

        return ProbeFileSystemIgnoreCase(projectRoot);
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
        var output = TryRunGit(projectRoot, cancellationToken, "-c", "core.quotePath=false", "status", "--porcelain");
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

    private readonly record struct GitProcessCaptureResult(
        int? ExitCode,
        string Output,
        string Error,
        GitCommandFailureKind FailureKind,
        string? Diagnostic);

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
                var diagnostic = FormatGitDiagnostic(TrustedGitUnavailableMessage);
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
            var diagnostic = FormatGitDiagnostic($"git helper exception: {DiagnosticRedactor.ClassifyException(ex)}: {ex.Message}");
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

    // Drain stdout and stderr concurrently at stream level so a newline-free chunk is capped
    // before framework line buffering can accumulate unbounded data. Returns null if the
    // process fails to start; otherwise the caller decides how to interpret the exit code.
    // stdout/stderr を stream level で同時に汲み出し、改行なしの巨大 chunk でも framework の
    // 行バッファに溜まる前に上限を強制する。
    private static (int ExitCode, string Output, string Error)? RunProcessCapturingOutput(
        ProcessStartInfo psi,
        CancellationToken cancellationToken = default)
    {
        var result = RunProcessCapturingResult(psi, cancellationToken);
        if (result == null || result.Value.FailureKind == GitCommandFailureKind.StartFailed)
            return null;

        return (result.Value.ExitCode ?? GitProcessFailureExitCode, result.Value.Output, result.Value.Error);
    }

    private static GitProcessCaptureResult? RunProcessCapturingResult(
        ProcessStartInfo psi,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        GitCommandFailureKind failureKind = GitCommandFailureKind.None;
        string? failureDiagnostic = null;
        var failureLock = new object();

        void MarkFailure(GitCommandFailureKind kind, string diagnostic)
        {
            lock (failureLock)
            {
                if (failureKind != GitCommandFailureKind.None)
                    return;
                failureKind = kind;
                failureDiagnostic = FormatGitDiagnostic(diagnostic);
            }
            TryKillProcessTree(process);
        }

        try
        {
            if (!process.Start())
            {
                var diagnostic = FormatGitDiagnostic("git process did not start.");
                return new GitProcessCaptureResult(
                    null,
                    string.Empty,
                    diagnostic,
                    GitCommandFailureKind.StartFailed,
                    diagnostic);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            var diagnostic = FormatGitDiagnostic($"git process start failed: {DiagnosticRedactor.ClassifyException(ex)}: {ex.Message}");
            return new GitProcessCaptureResult(
                null,
                string.Empty,
                diagnostic,
                GitCommandFailureKind.StartFailed,
                diagnostic);
        }

        var stdoutTask = Task.Run(() => ReadCapturedStream(process.StandardOutput, stdout, "stdout", MarkFailure));
        var stderrTask = Task.Run(() => ReadCapturedStream(process.StandardError, stderr, "stderr", MarkFailure));
        var exited = WaitForGitExit(process, GitCommandTimeout, cancellationToken, out var cancelled);
        if (!exited)
        {
            MarkFailure(
                cancelled ? GitCommandFailureKind.Cancelled : GitCommandFailureKind.TimedOut,
                cancelled
                ? "git command cancelled."
                : $"git command timed out after {FormatDuration(GitCommandTimeout)}.");
            if (!process.WaitForExit(ToWaitMilliseconds(GitKillWaitTimeout)))
            {
                if (cancelled)
                    cancellationToken.ThrowIfCancellationRequested();
                return new GitProcessCaptureResult(
                    GitProcessFailureExitCode,
                    ReadCaptured(stdout),
                    CombineCapturedError(ReadCaptured(stderr), failureDiagnostic!),
                    failureKind,
                    failureDiagnostic);
            }
        }
        else
        {
            _ = process.WaitForExit(0);
        }
        if (!WaitForCaptureReaders(stdoutTask, stderrTask))
            MarkFailure(GitCommandFailureKind.OutputCaptureIncomplete, "git command output capture did not finish.");

        var output = ReadCaptured(stdout);
        var error = ReadCaptured(stderr);
        if (cancelled)
            cancellationToken.ThrowIfCancellationRequested();
        if (failureKind != GitCommandFailureKind.None)
            return new GitProcessCaptureResult(
                GitProcessFailureExitCode,
                output,
                CombineCapturedError(error, failureDiagnostic!),
                failureKind,
                failureDiagnostic);

        var exitCode = process.ExitCode;
        if (exitCode != 0)
        {
            var diagnostic = FormatGitDiagnostic(string.IsNullOrWhiteSpace(error)
                ? $"git command exited with {exitCode.ToString(CultureInfo.InvariantCulture)} and no stderr."
                : error);
            return new GitProcessCaptureResult(
                exitCode,
                output,
                diagnostic,
                GitCommandFailureKind.ExitCode,
                diagnostic);
        }

        return new GitProcessCaptureResult(exitCode, output, error, GitCommandFailureKind.None, null);
    }

    private static bool WaitForGitExit(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        out bool cancelled)
    {
        var timeoutMilliseconds = ToWaitMilliseconds(timeout);
        var waitSliceMilliseconds = Math.Min(50, timeoutMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (process.WaitForExit(waitSliceMilliseconds))
            {
                cancelled = false;
                return true;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                return false;
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                cancelled = false;
                return false;
            }
        }
    }

    private static string ReadCaptured(StringBuilder builder)
    {
        lock (builder)
            return builder.ToString();
    }

    private static bool WaitForCaptureReaders(Task stdoutTask, Task stderrTask)
    {
        try
        {
            return Task.WaitAll([stdoutTask, stderrTask], ToWaitMilliseconds(GitKillWaitTimeout));
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private static void ReadCapturedStream(
        TextReader reader,
        StringBuilder builder,
        string streamName,
        Action<GitCommandFailureKind, string> markFailure)
    {
        var buffer = new char[GitCaptureReadBufferChars];
        try
        {
            while (true)
            {
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    return;
                if (!AppendBoundedCapturedChars(builder, buffer.AsSpan(0, read), streamName, markFailure))
                    return;
            }
        }
        catch (IOException ex)
        {
            markFailure(
                GitCommandFailureKind.CaptureFailed,
                $"git command {streamName} read failed: {DiagnosticRedactor.ClassifyException(ex)}");
        }
        catch (ObjectDisposedException)
        {
            // Process cleanup closed the stream after another failure path.
        }
    }

    private static bool AppendBoundedCapturedChars(
        StringBuilder builder,
        ReadOnlySpan<char> data,
        string streamName,
        Action<GitCommandFailureKind, string> markFailure)
    {
        lock (builder)
        {
            var remaining = MaxCapturedGitOutputChars - builder.Length;
            if (remaining <= 0)
            {
                markFailure(GitCommandFailureKind.CaptureLimitExceeded, BuildCaptureLimitMessage(streamName));
                return false;
            }

            if (data.Length <= remaining)
            {
                builder.Append(data);
                return true;
            }

            builder.Append(data[..Math.Min(data.Length, remaining)]);
        }

        markFailure(GitCommandFailureKind.CaptureLimitExceeded, BuildCaptureLimitMessage(streamName));
        return false;
    }

    private static string BuildCaptureLimitMessage(string streamName)
        => $"git command captured {streamName} exceeded {MaxCapturedGitOutputChars.ToString(CultureInfo.InvariantCulture)} characters.";

    private static string FormatGitDiagnostic(string diagnostic)
        => DiagnosticRedactor.BoundDiagnosticText(
            DiagnosticRedactor.RedactSensitiveText(diagnostic, "[redacted]", redactPaths: true),
            MaxGitFailureDiagnosticChars);

    private static string CombineCapturedError(string stderr, string diagnostic)
        => FormatGitDiagnostic(string.IsNullOrWhiteSpace(stderr)
            ? diagnostic
            : stderr.TrimEnd('\r', '\n') + "\n" + diagnostic);

    private static int ToWaitMilliseconds(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            return 1;
        if (timeout.TotalMilliseconds >= int.MaxValue)
            return int.MaxValue;
        return Math.Max(1, (int)Math.Ceiling(timeout.TotalMilliseconds));
    }

    private static string FormatDuration(TimeSpan timeout)
        => timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort cleanup only; callers receive the timeout/capture diagnostic.
        }
    }

    private static bool ProbeFileSystemIgnoreCase(string projectRoot)
    {
        var normalizedRoot = projectRoot;
        try
        {
            normalizedRoot = Path.GetFullPath(projectRoot);
            if (FileSystemIgnoreCaseProbeForTesting is { } probeOverride)
                return probeOverride(normalizedRoot);

            if (TryProbeExistingDirectoryPath(normalizedRoot, out var ignoreCase))
                return ignoreCase;

            using var probe = CaseSensitivityProbeDirectory.CreateProbePathScope(normalizedRoot, "case-probe-");
            var probePath = probe.Path;
            FileWriteProbe.WriteEmptyFile(probePath);
            try
            {
                if (TryCreateCaseVariant(probePath, out var variant))
                    return File.Exists(LongPath.EnsureWindowsPrefix(variant));
            }
            finally
            {
                FileWriteProbe.DeleteFileIfExists(probePath);
            }

            throw new CaseSensitivityProbeException(
                "Failed to create a case-variant path for filesystem case-sensitivity probing.",
                normalizedRoot,
                probePath: probePath);
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

    private static bool TryProbeExistingDirectoryPath(string path, out bool ignoreCase)
    {
        ignoreCase = false;
        if (!TryCreateCaseVariant(path, out var variant))
            return false;

        ignoreCase = Directory.Exists(LongPath.EnsureWindowsPrefix(variant));
        return true;
    }

    private static bool TryCreateCaseVariant(string path, out string variant)
    {
        var chars = path.ToCharArray();
        for (var i = chars.Length - 1; i >= 0; i--)
        {
            var ch = chars[i];
            if (!char.IsLetter(ch))
                continue;

            chars[i] = char.IsUpper(ch)
                ? char.ToLowerInvariant(ch)
                : char.ToUpperInvariant(ch);
            variant = new string(chars);
            return true;
        }

        variant = path;
        return false;
    }
}
