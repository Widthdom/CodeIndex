using System.Diagnostics;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for GitHelper.ResolveGitCommonDir.
/// GitHelper.ResolveGitCommonDirのテスト。
/// </summary>
public class GitHelperTests : IDisposable
{
    private readonly string _tempDir;

    public GitHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            DeleteDirectoryRobust(_tempDir);
    }

    [Fact]
    public void NormalRepo_ReturnsGitDirectory()
    {
        // Arrange: create a normal .git directory / 通常の.gitディレクトリを作成
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);

        // Act
        var result = GitHelper.ResolveGitCommonDir(_tempDir);

        // Assert
        Assert.Equal(gitDir, result);
    }

    [Fact]
    public void NoGitAtAll_ReturnsNull()
    {
        // No .git file or directory / .gitファイルもディレクトリもない
        var result = GitHelper.ResolveGitCommonDir(_tempDir);
        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithAbsolutePath_ResolvesCommonDir()
    {
        // Arrange: simulate a worktree structure / worktree構造をシミュレート
        // Main repo .git dir
        var mainGitDir = Path.Combine(_tempDir, "main_repo", ".git");
        Directory.CreateDirectory(Path.Combine(mainGitDir, "info"));
        Directory.CreateDirectory(Path.Combine(mainGitDir, "worktrees", "my-worktree"));

        // commondir file inside the worktree git dir points to the main .git
        var worktreeGitDir = Path.Combine(mainGitDir, "worktrees", "my-worktree");
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");

        // Worktree project directory with .git file
        var worktreeRoot = Path.Combine(_tempDir, "worktree_checkout");
        Directory.CreateDirectory(worktreeRoot);
        File.WriteAllText(Path.Combine(worktreeRoot, ".git"), $"gitdir: {worktreeGitDir}");

        // Act
        var result = GitHelper.ResolveGitCommonDir(worktreeRoot);

        // Assert: should resolve to the main .git directory
        Assert.Equal(Path.GetFullPath(mainGitDir), Path.GetFullPath(result!));
    }

    [Fact]
    public void WorktreeWithRelativePath_ResolvesCommonDir()
    {
        // Arrange: simulate worktree with relative gitdir path / 相対パスのgitdirでworktreeをシミュレート
        var mainGitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(Path.Combine(mainGitDir, "info"));
        Directory.CreateDirectory(Path.Combine(mainGitDir, "worktrees", "feat-branch"));

        var worktreeGitDir = Path.Combine(mainGitDir, "worktrees", "feat-branch");
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), "../..");

        // Worktree is a sibling directory
        var worktreeRoot = Path.Combine(_tempDir, "worktree-feat");
        Directory.CreateDirectory(worktreeRoot);
        // Use relative path from worktree root to worktree git dir
        File.WriteAllText(Path.Combine(worktreeRoot, ".git"),
            $"gitdir: ../.git/worktrees/feat-branch");

        // Act
        var result = GitHelper.ResolveGitCommonDir(worktreeRoot);

        // Assert
        Assert.Equal(Path.GetFullPath(mainGitDir), Path.GetFullPath(result!));
    }

    [Fact]
    public void GitFileWithInvalidContent_ReturnsNull()
    {
        // .git file exists but doesn't start with "gitdir:" / .gitファイルがあるが"gitdir:"で始まらない
        File.WriteAllText(Path.Combine(_tempDir, ".git"), "some random content");

        var result = GitHelper.ResolveGitCommonDir(_tempDir);
        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithoutCommonDir_FallsBackToWorktreeGitDir()
    {
        // Arrange: worktree git dir exists but has no commondir file / commondirファイルがない場合のフォールバック
        var worktreeGitDir = Path.Combine(_tempDir, "fake-git-dir");
        Directory.CreateDirectory(worktreeGitDir);

        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, ".git"), $"gitdir: {worktreeGitDir}");

        // Act
        var result = GitHelper.ResolveGitCommonDir(projectRoot);

        // Assert: falls back to the worktree git dir itself
        Assert.Equal(Path.GetFullPath(worktreeGitDir), Path.GetFullPath(result!));
    }

    [Fact]
    public void GetChangedFilesFromCommit_ReturnsFilesForRegularCommit()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v1\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");

        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v2\n");
        File.WriteAllText(Path.Combine(repoDir, "added.txt"), "new\n");
        RunGit(repoDir, "add", "tracked.txt", "added.txt");
        RunGit(repoDir, "commit", "-m", "update files");

        var commitId = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        var changedFiles = GitHelper.GetChangedFilesFromCommit(repoDir, commitId);

        Assert.Equal(["added.txt", "tracked.txt"], changedFiles.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void GetChangedFilesFromCommit_IncludesFilesForRootCommit()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "first.txt"), "hello\n");
        RunGit(repoDir, "add", "first.txt");
        RunGit(repoDir, "commit", "-m", "initial");

        var commitId = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        var changedFiles = GitHelper.GetChangedFilesFromCommit(repoDir, commitId);

        Assert.Equal(["first.txt"], changedFiles);
    }

    [Fact]
    public void GetChangedFilesFromCommit_ReturnsFilesForMergeCommit()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "base.txt"), "base\n");
        RunGit(repoDir, "add", "base.txt");
        RunGit(repoDir, "commit", "-m", "base");
        var baseBranch = RunGit(repoDir, "branch", "--show-current").Trim();

        RunGit(repoDir, "switch", "-c", "feature");
        File.WriteAllText(Path.Combine(repoDir, "feature.txt"), "feature\n");
        RunGit(repoDir, "add", "feature.txt");
        RunGit(repoDir, "commit", "-m", "feature change");

        RunGit(repoDir, "switch", baseBranch);
        File.WriteAllText(Path.Combine(repoDir, "main.txt"), "main\n");
        RunGit(repoDir, "add", "main.txt");
        RunGit(repoDir, "commit", "-m", "main change");

        RunGit(repoDir, "merge", "--no-ff", "feature", "-m", "merge feature");
        var commitId = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        var changedFiles = GitHelper.GetChangedFilesFromCommit(repoDir, commitId);

        Assert.Equal(["feature.txt", "main.txt"], changedFiles.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void GetChangedFilesFromCommit_IncludesOldAndNewPathsForRenameCommit()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "old.txt"), "v1\n");
        RunGit(repoDir, "add", "old.txt");
        RunGit(repoDir, "commit", "-m", "initial");

        File.Move(Path.Combine(repoDir, "old.txt"), Path.Combine(repoDir, "new.txt"));
        File.AppendAllText(Path.Combine(repoDir, "new.txt"), "v2\n");
        RunGit(repoDir, "add", "-A");
        RunGit(repoDir, "commit", "-m", "rename file");

        var commitId = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        var changedFiles = GitHelper.GetChangedFilesFromCommit(repoDir, commitId);

        Assert.Contains("old.txt", changedFiles);
        Assert.Contains("new.txt", changedFiles);
    }

    [Fact]
    public void TryGetHeadCommit_ReturnsHeadCommitForRepo()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v1\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");

        var expected = RunGit(repoDir, "rev-parse", "HEAD").Trim();
        var actual = GitHelper.TryGetHeadCommit(repoDir);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryIsWorktreeDirty_DetectsModifiedFiles()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v1\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");

        Assert.False(GitHelper.TryIsWorktreeDirty(repoDir));

        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v2\n");

        Assert.True(GitHelper.TryIsWorktreeDirty(repoDir));
    }

    [Fact]
    public void ResolveIgnoreCase_UsesGitConfigWhenRepositorySetsTrue()
    {
        var repoDir = CreateGitRepo();
        RunGit(repoDir, "config", "core.ignorecase", "true");

        Assert.True(GitHelper.ResolveIgnoreCase(repoDir));
    }

    [Fact]
    public void ResolveIgnoreCase_UsesGitConfigWhenRepositorySetsFalse()
    {
        var repoDir = CreateGitRepo();
        RunGit(repoDir, "config", "core.ignorecase", "false");

        Assert.False(GitHelper.ResolveIgnoreCase(repoDir));
    }

    [Fact]
    public void ResolveIgnoreCase_UsesGitConfigWhenProjectPathIsSubdirectoryAndRepositorySetsTrue()
    {
        var repoDir = CreateGitRepo();
        var subDir = Path.Combine(repoDir, "src", "module");
        Directory.CreateDirectory(subDir);
        RunGit(repoDir, "config", "core.ignorecase", "true");

        Assert.True(GitHelper.ResolveIgnoreCase(subDir));
    }

    [Fact]
    public void ResolveIgnoreCase_UsesGitConfigWhenProjectPathIsSubdirectoryAndRepositorySetsFalse()
    {
        var repoDir = CreateGitRepo();
        var subDir = Path.Combine(repoDir, "src", "module");
        Directory.CreateDirectory(subDir);
        RunGit(repoDir, "config", "core.ignorecase", "false");

        Assert.False(GitHelper.ResolveIgnoreCase(subDir));
    }

    [Fact]
    public void ResolveIgnoreCase_NonRepoIgnoresGlobalGitConfigAndFallsBackToFileSystemProbe()
    {
        var nonRepoDir = Path.Combine(_tempDir, $"non_repo_{Guid.NewGuid():N}");
        var fakeHome = Path.Combine(_tempDir, $"fake_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(nonRepoDir);
        Directory.CreateDirectory(fakeHome);

        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = fakeHome,
            ["XDG_CONFIG_HOME"] = Path.Combine(fakeHome, ".config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };

        RunGitWithEnvironment(fakeHome, environment, "config", "--global", "core.ignorecase", "false");

        var resolved = GitHelper.ResolveIgnoreCase(nonRepoDir, environment);
        var expected = ProbeDirectoryIgnoreCaseLikeProduction(nonRepoDir);

        Assert.Equal(expected, resolved);
    }

    private string CreateGitRepo()
    {
        var repoDir = Path.Combine(_tempDir, $"repo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);

        RunGit(repoDir, "init");
        RunGit(repoDir, "config", "user.name", "CodeIndex Tests");
        RunGit(repoDir, "config", "user.email", "tests@example.com");

        return repoDir;
    }

    private static string RunGit(string workDir, params string[] args)
        => RunGitWithEnvironment(workDir, environment: null, args);

    private static string RunGitWithEnvironment(string workDir, IReadOnlyDictionary<string, string?>? environment, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                if (value == null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;
            }
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process / gitプロセスの起動に失敗");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Trim()}");

        return stdout;
    }

    private static bool ProbeDirectoryIgnoreCaseLikeProduction(string path)
    {
        if (TryCreateCaseVariant(path, out var variant))
            return Directory.Exists(variant);

        var probePath = Path.Combine(path, $".cdidx_case_probe_test_{Guid.NewGuid():N}");
        File.WriteAllText(probePath, string.Empty);
        try
        {
            return TryCreateCaseVariant(probePath, out var probeVariant) && File.Exists(probeVariant);
        }
        finally
        {
            if (File.Exists(probePath))
                File.Delete(probePath);
        }
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

    private static void DeleteDirectoryRobust(string path)
    {
        ClearAttributes(path);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
                ClearAttributes(path);
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
                ClearAttributes(path);
            }
        }
    }

    private static void ClearAttributes(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);

        foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(dir, FileAttributes.Normal);

        File.SetAttributes(path, FileAttributes.Normal);
    }
}
