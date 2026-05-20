using System.Diagnostics;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for GitHelper.ResolveGitCommonDir.
/// GitHelper.ResolveGitCommonDirのテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
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
    public void ResolveGitCommonDir_BareRepository_ReturnsRepositoryDirectory()
    {
        var sourceRepo = CreateGitRepo();
        File.WriteAllText(Path.Combine(sourceRepo, "tracked.txt"), "v1\n");
        RunGit(sourceRepo, "add", "tracked.txt");
        RunGit(sourceRepo, "commit", "-m", "initial");
        var bareRepo = Path.Combine(_tempDir, "repo.git");
        RunGit(_tempDir, "clone", "--bare", sourceRepo, bareRepo);

        Assert.Equal(GitRepositoryType.Bare, GitHelper.TryGetRepositoryType(bareRepo));
        Assert.Equal(Path.GetFullPath(bareRepo), Path.GetFullPath(GitHelper.ResolveGitCommonDir(bareRepo)!));
        Assert.Equal(Path.GetFullPath(bareRepo), Path.GetFullPath(GitHelper.TryGetRepositoryRoot(bareRepo)!));
    }

    [Fact]
    public void TryGetRepositoryType_ClassifiesNormalWorktreeBareAndNone()
    {
        var normalRepo = CreateGitRepo();
        var linkedWorktree = Path.Combine(_tempDir, "linked-worktree");
        RunGit(normalRepo, "worktree", "add", linkedWorktree);
        var bareRepo = Path.Combine(_tempDir, "shape.git");
        RunGit(_tempDir, "clone", "--bare", normalRepo, bareRepo);
        var nonRepo = Path.Combine(_tempDir, "not-a-repo");
        Directory.CreateDirectory(nonRepo);

        Assert.Equal(GitRepositoryType.Normal, GitHelper.TryGetRepositoryType(normalRepo));
        Assert.Equal(GitRepositoryType.Worktree, GitHelper.TryGetRepositoryType(linkedWorktree));
        Assert.Equal(GitRepositoryType.Bare, GitHelper.TryGetRepositoryType(bareRepo));
        Assert.Equal(GitRepositoryType.None, GitHelper.TryGetRepositoryType(nonRepo));
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
    public void GetChangedFilesFromCommit_RejectsRevisionRanges()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "first.txt"), "hello\n");
        RunGit(repoDir, "add", "first.txt");
        RunGit(repoDir, "commit", "-m", "initial");
        File.WriteAllText(Path.Combine(repoDir, "second.txt"), "hello\n");
        RunGit(repoDir, "add", "second.txt");
        RunGit(repoDir, "commit", "-m", "second");

        var ex = Assert.Throws<ArgumentException>(
            () => GitHelper.GetChangedFilesFromCommit(repoDir, "HEAD~1..HEAD"));

        Assert.Contains("ranges and tag refs are not accepted", ex.Message);
    }

    [Fact]
    public void GetChangedFilesFromCommit_RejectsTagRefs()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "first.txt"), "hello\n");
        RunGit(repoDir, "add", "first.txt");
        RunGit(repoDir, "commit", "-m", "initial");
        RunGit(repoDir, "tag", "v1.0");

        var ex = Assert.Throws<ArgumentException>(
            () => GitHelper.GetChangedFilesFromCommit(repoDir, "v1.0"));

        Assert.Contains("Tag refs are not accepted", ex.Message);
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
    public void GetChangedFilesFromCommit_DeduplicatesFilesForOctopusMergeCommit()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "base.txt"), "base\n");
        RunGit(repoDir, "add", "base.txt");
        RunGit(repoDir, "commit", "-m", "base");
        var baseBranch = RunGit(repoDir, "branch", "--show-current").Trim();

        RunGit(repoDir, "switch", "-c", "feature-one");
        File.WriteAllText(Path.Combine(repoDir, "one.txt"), "one\n");
        RunGit(repoDir, "add", "one.txt");
        RunGit(repoDir, "commit", "-m", "feature one");

        RunGit(repoDir, "switch", baseBranch);
        RunGit(repoDir, "switch", "-c", "feature-two");
        File.WriteAllText(Path.Combine(repoDir, "two.txt"), "two\n");
        RunGit(repoDir, "add", "two.txt");
        RunGit(repoDir, "commit", "-m", "feature two");

        RunGit(repoDir, "switch", baseBranch);
        RunGit(repoDir, "switch", "-c", "feature-three");
        File.WriteAllText(Path.Combine(repoDir, "three.txt"), "three\n");
        RunGit(repoDir, "add", "three.txt");
        RunGit(repoDir, "commit", "-m", "feature three");

        RunGit(repoDir, "switch", baseBranch);
        RunGit(repoDir, "merge", "--no-ff", "feature-one", "feature-two", "feature-three", "-m", "octopus merge");
        var commitId = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        var changedFiles = GitHelper.GetChangedFilesFromCommit(repoDir, commitId);

        Assert.Equal(["one.txt", "three.txt", "two.txt"], changedFiles.OrderBy(x => x).ToArray());
        Assert.Equal(changedFiles.Count, changedFiles.Distinct(StringComparer.Ordinal).Count());
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
    public async Task GetChangedFilesFromCommit_DrainsLargeStderrWithoutDeadlock()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatEmitsLargeStderr(fakeGitDir);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeGitDir + Path.PathSeparator + oldPath);
        try
        {
            var task = Task.Run(() => GitHelper.GetChangedFilesFromCommit(repoDir, "0123456789abcdef"));

            var changedFiles = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["changed.txt"], changedFiles);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Theory]
    [InlineData("feature")]
    [InlineData("v1.0.0")]
    [InlineData("main..feature")]
    public void GetChangedFilesFromCommit_RejectsNonCommitIdRefs(string commitRef)
    {
        var repoDir = CreateGitRepo();

        var ex = Assert.Throws<ArgumentException>(() => GitHelper.GetChangedFilesFromCommit(repoDir, commitRef));

        Assert.Contains("Invalid commit ID", ex.Message);
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
    public void TryGetHeadBranch_ReturnsBranchShortName()
    {
        var repoDir = CreateGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v1\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");

        // Force a deterministic branch name so the assertion isn't sensitive to the
        // local `init.defaultBranch` setting on the dev machine.
        // ローカル設定の影響を避けるためブランチを明示的に切り替える。
        RunGit(repoDir, "switch", "-c", "feature");

        Assert.Equal("feature", GitHelper.TryGetHeadBranch(repoDir));
    }

    [Fact]
    public void TryGetHeadBranch_ReturnsNullOnDetachedHead()
    {
        var repoDir = CreateGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v1\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");
        var sha = RunGit(repoDir, "rev-parse", "HEAD").Trim();
        // `git checkout <sha>` detaches HEAD; rev-parse --abbrev-ref then prints "HEAD".
        // We must not surface that literal "HEAD" as a real branch name. Issue #1509.
        // detached HEAD では文字列 "HEAD" を branch 名として誤って返さないことを保証する。
        RunGit(repoDir, "checkout", "--detach", sha);

        Assert.Null(GitHelper.TryGetHeadBranch(repoDir));
    }

    [Fact]
    public void TryCountCommitsAhead_ReturnsZeroWhenIndexedShaEqualsCurrent()
    {
        var repoDir = CreateGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v1\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");
        var sha = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        Assert.Equal(0, GitHelper.TryCountCommitsAhead(repoDir, sha));
    }

    [Fact]
    public void TryCountCommitsAhead_CountsCommitsBetweenIndexedAndCurrentHead()
    {
        var repoDir = CreateGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v1\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");
        var indexedSha = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v2\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "second");
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v3\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "third");

        Assert.Equal(2, GitHelper.TryCountCommitsAhead(repoDir, indexedSha));
    }

    [Fact]
    public void TryCountCommitsAhead_ReturnsNullWhenIndexedShaIsNotAncestor()
    {
        var repoDir = CreateGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "base\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "base");
        var defaultBranch = RunGit(repoDir, "rev-parse", "--abbrev-ref", "HEAD").Trim();

        // Create a divergent commit, capture its SHA, then switch back to the
        // original branch so the diverged commit is no longer reachable from HEAD.
        // "Ahead by N" is not meaningful here, so the helper must report null
        // instead of a misleading 0.
        // 非祖先 commit に対しては「N コミット進んでいる」は意味を成さないので null を返す。
        RunGit(repoDir, "switch", "-c", "divergent");
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "divergent\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "divergent");
        var divergentSha = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        // Switch back to the original branch and add another commit on its lineage.
        RunGit(repoDir, "switch", defaultBranch);
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "after\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "after");

        Assert.Null(GitHelper.TryCountCommitsAhead(repoDir, divergentSha));
    }

    [Fact]
    public void TryCountCommitsAhead_RejectsArgumentInjectionAttempts()
    {
        var repoDir = CreateGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "v1\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");

        // The helper must reject values that look like git options, mirroring the
        // existing GetChangedFilesFromCommit validation, so a caller cannot smuggle
        // `--exec` or similar payloads through the stamped indexed_head_sha.
        // 永続化された stamp 経由で git オプションが流れ込まないよう dash 始まりを拒否する。
        Assert.Null(GitHelper.TryCountCommitsAhead(repoDir, "--upload-pack=evil"));
        Assert.Null(GitHelper.TryCountCommitsAhead(repoDir, string.Empty));
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
    public void TryGetWorktreeStatus_DetectsUnresolvedMergeFiles()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "base\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "initial");
        var defaultBranch = RunGit(repoDir, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        RunGit(repoDir, "switch", "-c", "feature");
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "feature\n");
        RunGit(repoDir, "commit", "-am", "feature");
        RunGit(repoDir, "switch", defaultBranch);
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "main\n");
        RunGit(repoDir, "commit", "-am", "main");

        Assert.Throws<InvalidOperationException>(() => RunGit(repoDir, "merge", "feature"));

        var status = GitHelper.TryGetWorktreeStatus(repoDir);

        Assert.NotNull(status);
        Assert.True(status.IsDirty);
        Assert.Contains("tracked.txt", status.UnresolvedMergeFiles);
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

    private static void WriteFakeGitThatEmitsLargeStderr(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, """
#!/bin/sh
if [ "$1" = "rev-parse" ]; then
  if [ "$2" = "--symbolic-full-name" ]; then
    exit 0
  fi
  if [ "$2" = "--verify" ]; then
    printf '%s\n' '0123456789abcdef0123456789abcdef01234567'
    exit 0
  fi
fi
if [ "$1" = "diff-tree" ]; then
  perl -e 'print STDERR "x" x 131072'
  printf 'M\tchanged.txt\n'
  exit 0
fi
exit 1
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
