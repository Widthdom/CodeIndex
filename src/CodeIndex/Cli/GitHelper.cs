using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

/// <summary>
/// Git integration helpers.
/// Git連携ヘルパー。
/// </summary>
public static class GitHelper
{
    /// <summary>
    /// Resolve the common git directory for a project root, handling both normal repos and worktrees.
    /// プロジェクトルートの共通gitディレクトリを解決する。通常リポジトリとworktreeの両方に対応。
    /// In a normal repo, .git is a directory and is returned directly.
    /// In a worktree, .git is a file containing "gitdir: path/to/.git/worktrees/name".
    /// The common dir is resolved via the "commondir" file inside the worktree git dir.
    /// </summary>
    public static string? ResolveGitCommonDir(string projectRoot)
    {
        var dotGit = Path.Combine(projectRoot, ".git");
        var ioDotGit = LongPath.EnsureWindowsPrefix(dotGit);

        // Normal repository: .git is a directory / 通常リポジトリ: .gitがディレクトリ
        if (Directory.Exists(ioDotGit)) return dotGit;

        // Worktree: .git is a file containing "gitdir: <path>" / worktree: .gitがファイルで "gitdir: <path>" を含む
        if (!File.Exists(ioDotGit)) return null;

        var gitFileContent = File.ReadAllText(ioDotGit).Trim();
        if (!gitFileContent.StartsWith("gitdir:")) return null;

        var worktreeGitDir = gitFileContent["gitdir:".Length..].Trim();
        if (!Path.IsPathRooted(worktreeGitDir))
            worktreeGitDir = Path.GetFullPath(Path.Combine(projectRoot, worktreeGitDir));

        // Read commondir to find the shared .git directory / commondirを読んで共有.gitディレクトリを見つける
        var commonDirFile = Path.Combine(worktreeGitDir, "commondir");
        var ioCommonDirFile = LongPath.EnsureWindowsPrefix(commonDirFile);
        if (File.Exists(ioCommonDirFile))
        {
            var commonDirRelative = File.ReadAllText(ioCommonDirFile).Trim();
            return Path.GetFullPath(Path.Combine(worktreeGitDir, commonDirRelative));
        }

        // Fallback: use worktreeGitDir itself (e.g. submodules) / フォールバック: worktreeGitDir自体を使用
        return worktreeGitDir;
    }

    /// <summary>
    /// Get changed files from a git commit.
    /// gitコミットから変更ファイルを取得する。
    /// </summary>
    public static List<string> GetChangedFilesFromCommit(string projectRoot, string commitId)
    {
        // Validate commit ID to prevent argument injection (only hex + common ref chars allowed)
        // Reject values starting with "-" to prevent git option injection even without "--" separator
        // コミットIDをバリデーションし引数インジェクションを防止（16進数+一般的な参照文字のみ許可）
        // "-"で始まる値も拒否し、"--"セパレータなしでもgitオプション注入を防止
        if (commitId.StartsWith('-') || !Regex.IsMatch(commitId, @"^[a-zA-Z0-9_./^~\-]+$"))
            throw new ArgumentException($"Invalid commit ID: {commitId}");

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("diff-tree");
        psi.ArgumentList.Add("--no-commit-id");
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("--name-only");
        psi.ArgumentList.Add(commitId);

        var (exitCode, output, error) = RunProcessCapturingOutput(psi)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");

        if (exitCode != 0)
            throw new InvalidOperationException($"git diff-tree failed for commit {commitId}: {error.Trim()}");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(FileIndexer.NormalizePathSeparators)
            .ToList();
    }

    /// <summary>
    /// Get changed files between two git refs, including both sides of renames.
    /// 2つのgit ref間の変更ファイルを取得する。rename は旧パスと新パスの両方を含める。
    /// </summary>
    public static List<string> GetChangedFilesBetweenRefs(string projectRoot, string oldRef, string newRef)
    {
        ValidateGitRef(oldRef, nameof(oldRef));
        ValidateGitRef(newRef, nameof(newRef));

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("diff");
        psi.ArgumentList.Add("--name-status");
        psi.ArgumentList.Add("-M");
        psi.ArgumentList.Add(oldRef);
        psi.ArgumentList.Add(newRef);
        psi.ArgumentList.Add("--");

        var (exitCode, output, error) = RunProcessCapturingOutput(psi)
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
    public static string? TryGetHeadCommit(string projectRoot)
    {
        var output = TryRunGit(projectRoot, "rev-parse", "HEAD");
        var value = output?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Try to resolve the current branch short name. Returns null on detached HEAD
    /// (`git rev-parse --abbrev-ref HEAD` prints `HEAD` in that state, which we treat
    /// as "no branch" so callers can render it as detached without misclassifying it
    /// as the literal branch name "HEAD"). Issue #1509.
    /// 現在のブランチ短縮名を安全に取得する。detached HEAD は null 扱いにして、
    /// 文字列 "HEAD" を誤ってブランチ名として永続化しないようにする。
    /// </summary>
    public static string? TryGetHeadBranch(string projectRoot)
    {
        var output = TryRunGit(projectRoot, "rev-parse", "--abbrev-ref", "HEAD");
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
    public static int? TryCountCommitsAhead(string projectRoot, string baseCommit)
    {
        if (string.IsNullOrWhiteSpace(baseCommit))
            return null;
        if (baseCommit.StartsWith('-') || !Regex.IsMatch(baseCommit, @"^[a-zA-Z0-9_./^~\-]+$"))
            return null;

        var headSha = TryGetHeadCommit(projectRoot);
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
        if (!TryRunGitForExitCode(projectRoot, "merge-base", "--is-ancestor", baseCommit, "HEAD"))
            return null;

        var output = TryRunGit(projectRoot, "rev-list", "--count", $"{baseCommit}..HEAD");
        if (output == null)
            return null;
        var trimmed = output.Trim();
        return int.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
    }

    private static bool TryRunGitForExitCode(string projectRoot, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            // Reuse the shared event-driven drainer (PR #1497) so we don't reintroduce
            // sync-over-async on git's stderr pipe. We only care about exit code here.
            // #1497 で導入した共有 drainer を使い、stderr の sync-over-async を再導入しない。
            var result = RunProcessCapturingOutput(psi);
            return result != null && result.Value.ExitCode == 0;
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
    public static string? TryGetRepositoryRoot(string projectPath)
        => TryGetRepositoryRoot(projectPath, gitEnvironmentOverrides: null);

    /// <summary>
    /// Resolve whether ignore matching should be case-insensitive for this workspace.
    /// git 管理下なら core.ignorecase を優先し、そうでなければファイルシステム特性を推定する。
    /// </summary>
    public static bool ResolveIgnoreCase(string projectRoot)
        => ResolveIgnoreCase(projectRoot, gitEnvironmentOverrides: null);

    internal static bool ResolveIgnoreCase(string projectRoot, IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides)
    {
        var repoRoot = TryGetRepositoryRoot(projectRoot, gitEnvironmentOverrides);
        if (repoRoot == null)
            return ProbeFileSystemIgnoreCase(projectRoot);

        var configured = TryRunGit(repoRoot, gitEnvironmentOverrides, "config", "--bool", "--get", "core.ignorecase")?.Trim();
        if (bool.TryParse(configured, out var ignoreCase))
            return ignoreCase;

        return ProbeFileSystemIgnoreCase(projectRoot);
    }

    /// <summary>
    /// Try to determine whether the worktree has uncommitted changes.
    /// worktree に未コミット変更があるか安全に判定する。
    /// </summary>
    public static bool? TryIsWorktreeDirty(string projectRoot)
    {
        var output = TryRunGit(projectRoot, "status", "--porcelain");
        return output == null ? null : output.Length > 0;
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
    public static HashSet<string>? TryGetSkipWorktreePaths(string projectRoot)
        => TryGetSkipWorktreePaths(projectRoot, gitEnvironmentOverrides: null);

    internal static HashSet<string>? TryGetSkipWorktreePaths(
        string projectRoot,
        IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides)
    {
        var output = TryRunGit(
            projectRoot,
            gitEnvironmentOverrides,
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

    internal static string? TryGetRepositoryRoot(string projectPath, IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides)
    {
        var cdup = TryRunGit(projectPath, gitEnvironmentOverrides, "rev-parse", "--show-cdup");
        if (cdup == null)
            return null;

        var value = cdup.Trim();
        return string.IsNullOrEmpty(value)
            ? Path.GetFullPath(projectPath)
            : Path.GetFullPath(Path.Combine(projectPath, value));
    }

    private static string? TryRunGit(string projectRoot, params string[] args)
        => TryRunGit(projectRoot, gitEnvironmentOverrides: null, args);

    private static string? TryRunGit(string projectRoot, IReadOnlyDictionary<string, string?>? gitEnvironmentOverrides, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = projectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

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

            var result = RunProcessCapturingOutput(psi);
            if (result == null)
                return null;

            var (exitCode, output, _) = result.Value;
            return exitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    // Drain stdout and stderr concurrently via Process's own event-based reader threads so a
    // full stderr pipe buffer cannot deadlock a blocking stdout read. Returns null if the
    // process fails to start; otherwise the caller decides how to interpret the exit code.
    // stdoutとstderrを同時に汲み出す。Process自前のイベントスレッドを使うことで
    // stderrパイプ満杯による stdout 読み取りデッドロックを防ぎ、
    // 非同期APIを GetAwaiter().GetResult() で待つ sync-over-async も避ける。
    private static (int ExitCode, string Output, string Error)? RunProcessCapturingOutput(ProcessStartInfo psi)
    {
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        // Always terminate captured lines with '\n' (not Environment.NewLine) so callers that
        // split on '\n' see identical output on Windows and POSIX — git writes LF-only to pipes.
        // キャプチャ行は常に '\n' 区切りにし、Windows/POSIX 双方で git のパイプ出力(LF)と一致させる。
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.Append(e.Data).Append('\n'); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.Append(e.Data).Append('\n'); };

        if (!process.Start())
            return null;

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static bool ProbeFileSystemIgnoreCase(string projectRoot)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(projectRoot);
            if (TryProbeExistingDirectoryPath(normalizedRoot, out var ignoreCase))
                return ignoreCase;

            var probePath = Path.Combine(normalizedRoot, $".cdidx_case_probe_{Guid.NewGuid():N}");
            var ioProbePath = LongPath.EnsureWindowsPrefix(probePath);
            File.WriteAllText(ioProbePath, string.Empty);
            try
            {
                if (TryCreateCaseVariant(probePath, out var variant))
                    return File.Exists(LongPath.EnsureWindowsPrefix(variant));
            }
            finally
            {
                if (File.Exists(ioProbePath))
                    File.Delete(ioProbePath);
            }
        }
        catch
        {
            // Best-effort fallback only / best-effort のフォールバックのみ
        }

        return OperatingSystem.IsWindows();
    }

    private static bool TryProbeExistingDirectoryPath(string path, out bool ignoreCase)
    {
        ignoreCase = false;
        if (!TryCreateCaseVariant(path, out var variant))
            return false;

        ignoreCase = Directory.Exists(variant);
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
