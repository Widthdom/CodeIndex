using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for GitHelper.ResolveGitCommonDir.
/// GitHelper.ResolveGitCommonDirのテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class GitHelperTests : IDisposable
{
    private const int FakeGitHangSeconds = 15;

    // Keep below the fake git scripts' sleep so missed timeout/cancellation still fails,
    // while leaving room for process cleanup under full-suite load.
    private static readonly TimeSpan GitCancellationWallClockLimit = TimeSpan.FromSeconds(10);

    private readonly string _tempDir;

    public GitHelperTests()
    {
        _tempDir = TestProjectHelper.CreateTempProject("cdidx_test");
    }

    public void Dispose()
    {
        TestProjectHelper.DeleteDirectory(_tempDir);
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
    public void GitMetadataEntrySymlinks_ReturnNull_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var externalGitDirectory = Path.Combine(_tempDir, "external-git-directory");
        Directory.CreateDirectory(externalGitDirectory);
        var directoryProject = Path.Combine(_tempDir, "directory-project");
        Directory.CreateDirectory(directoryProject);
        var directoryLink = Path.Combine(directoryProject, ".git");
        Directory.CreateSymbolicLink(directoryLink, externalGitDirectory);

        var externalGitFile = Path.Combine(_tempDir, "external-git-file");
        File.WriteAllText(externalGitFile, $"gitdir: {externalGitDirectory}");
        var fileProject = Path.Combine(_tempDir, "file-project");
        Directory.CreateDirectory(fileProject);
        var fileLink = Path.Combine(fileProject, ".git");
        File.CreateSymbolicLink(fileLink, externalGitFile);

        Assert.Null(GitHelper.ResolveGitCommonDir(directoryProject));
        Assert.Equal(GitRepositoryType.None, GitHelper.TryGetRepositoryType(directoryProject));
        Assert.Null(GitHelper.ResolveGitCommonDir(fileProject));
        Assert.Equal(GitRepositoryType.None, GitHelper.TryGetRepositoryType(fileProject));
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
    public void GitFileWithOversizedContent_ReturnsNull()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".git"), new string('x', GitHelper.MaxGitMetadataFileBytes + 1));

        var result = GitHelper.ResolveGitCommonDir(_tempDir);

        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithOversizedCommonDir_ReturnsNull()
    {
        var worktreeGitDir = Path.Combine(_tempDir, "fake-git-dir");
        Directory.CreateDirectory(worktreeGitDir);
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), new string('x', GitHelper.MaxGitMetadataFileBytes + 1));

        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, ".git"), $"gitdir: {worktreeGitDir}");

        var result = GitHelper.ResolveGitCommonDir(projectRoot);

        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithMissingGitDirTarget_ReturnsNull_Issue3813()
    {
        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, ".git"), $"gitdir: {Path.Combine(_tempDir, "missing-git-dir")}");

        var result = GitHelper.ResolveGitCommonDir(projectRoot);

        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithEscapingCommonDir_ReturnsNull_Issue3813()
    {
        var mainGitDir = Path.Combine(_tempDir, "main_repo", ".git");
        var worktreeGitDir = Path.Combine(mainGitDir, "worktrees", "escaping");
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(worktreeGitDir);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(worktreeGitDir, "commondir"), Path.GetRelativePath(worktreeGitDir, outside));

        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, ".git"), $"gitdir: {worktreeGitDir}");

        var result = GitHelper.ResolveGitCommonDir(projectRoot);

        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithSymlinkedMetadataDirectories_ReturnsNull_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var externalCommonDir = Path.Combine(_tempDir, "external-common-dir");
        var externalWorktreeGitDir = Path.Combine(externalCommonDir, "worktrees", "linked");
        Directory.CreateDirectory(externalWorktreeGitDir);
        File.WriteAllText(Path.Combine(externalWorktreeGitDir, "commondir"), "../..");

        var worktreeGitDirLink = Path.Combine(_tempDir, "worktree-git-dir-link");
        Directory.CreateSymbolicLink(worktreeGitDirLink, externalWorktreeGitDir);
        var directTargetProject = Path.Combine(_tempDir, "linked-target-project");
        Directory.CreateDirectory(directTargetProject);
        File.WriteAllText(Path.Combine(directTargetProject, ".git"), $"gitdir: {worktreeGitDirLink}");

        var commonDirLink = Path.Combine(_tempDir, "common-dir-link");
        Directory.CreateSymbolicLink(commonDirLink, externalCommonDir);
        var projectRoot = Path.Combine(_tempDir, "linked-worktree-project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(
            Path.Combine(projectRoot, ".git"),
            $"gitdir: {Path.Combine(commonDirLink, "worktrees", "linked")}");

        var directTargetResult = GitHelper.ResolveGitCommonDir(directTargetProject);
        var commonDirectoryResult = GitHelper.ResolveGitCommonDir(projectRoot);

        Assert.Null(directTargetResult);
        Assert.Null(commonDirectoryResult);
    }

    [Fact]
    public void WorktreeWithIntermediateSymlinkedMetadataComponent_ReturnsNull_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var realContainer = Path.Combine(_tempDir, "real-container");
        var realCommonDir = Path.Combine(realContainer, "repository", ".git");
        var realWorktreeGitDir = Path.Combine(realCommonDir, "worktrees", "linked");
        Directory.CreateDirectory(realWorktreeGitDir);
        File.WriteAllText(Path.Combine(realWorktreeGitDir, "commondir"), "../..");

        var containerLink = Path.Combine(_tempDir, "container-link");
        Directory.CreateSymbolicLink(containerLink, realContainer);
        var projectRoot = Path.Combine(_tempDir, "intermediate-link-project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(
            Path.Combine(projectRoot, ".git"),
            $"gitdir: {Path.Combine(containerLink, "repository", ".git", "worktrees", "linked")}");

        var result = GitHelper.ResolveGitCommonDir(projectRoot);

        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithDirectoryCommonDir_ReturnsNull_Issue3813()
    {
        var worktreeGitDir = Path.Combine(_tempDir, "fake-git-dir");
        Directory.CreateDirectory(Path.Combine(worktreeGitDir, "commondir"));

        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, ".git"), $"gitdir: {worktreeGitDir}");

        var result = GitHelper.ResolveGitCommonDir(projectRoot);

        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithExistingUnshapedGitDirTarget_ReturnsNull_Issue3813()
    {
        var unshapedGitDir = Path.Combine(_tempDir, "writable-target");
        Directory.CreateDirectory(unshapedGitDir);

        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, ".git"), $"gitdir: {unshapedGitDir}");

        var result = GitHelper.ResolveGitCommonDir(projectRoot);

        Assert.Null(result);
    }

    [Fact]
    public void WorktreeWithoutCommonDirAndValidGitDirShape_FallsBackToWorktreeGitDir()
    {
        // Arrange: gitdir exists without commondir but has a real git-dir shape.
        // commondir はないが、実際の git-dir 形状を持つ場合のフォールバック。
        var worktreeGitDir = Path.Combine(_tempDir, "fake-git-dir");
        Directory.CreateDirectory(Path.Combine(worktreeGitDir, "objects"));
        Directory.CreateDirectory(Path.Combine(worktreeGitDir, "refs"));
        File.WriteAllText(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/main\n");

        var projectRoot = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, ".git"), $"gitdir: {worktreeGitDir}");

        // Act
        var result = GitHelper.ResolveGitCommonDir(projectRoot);

        // Assert: falls back to the worktree git dir itself
        Assert.Equal(Path.GetFullPath(worktreeGitDir), Path.GetFullPath(result!));
    }

    [ExternalProcessFact]
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

    [ExternalProcessFact]
    public void TryGetRepositoryType_ClassifiesNormalWorktreeBareAndNone()
    {
        var normalRepo = CreateGitRepo();
        File.WriteAllText(Path.Combine(normalRepo, "tracked.txt"), "v1\n");
        RunGit(normalRepo, "add", "tracked.txt");
        RunGit(normalRepo, "commit", "-m", "initial");
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

    [ExternalProcessFact]
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

    [ExternalProcessFact]
    public void ChangedFileQueries_PreserveCanonicalUtf8Paths()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "base.txt"), "base\n");
        RunGit(repoDir, "add", "base.txt");
        RunGit(repoDir, "commit", "-m", "initial");
        var baseCommit = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        var unicodeDirectory = Path.Combine(repoDir, "日本");
        Directory.CreateDirectory(unicodeDirectory);
        File.WriteAllText(Path.Combine(unicodeDirectory, "é.cs"), "class Café { }\n");
        RunGit(repoDir, "add", ".");
        RunGit(repoDir, "commit", "-m", "add unicode path");
        var headCommit = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        const string expectedPath = "日本/é.cs";
        Assert.Equal([expectedPath], GitHelper.GetChangedFilesFromCommit(repoDir, headCommit));
        Assert.Equal([expectedPath], GitHelper.GetChangedFilesBetweenRefs(repoDir, baseCommit, headCommit));
    }

    [ExternalProcessFact]
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

    [ExternalProcessFact]
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

    [ExternalProcessFact]
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

    [ExternalProcessFact]
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

    [ExternalProcessFact]
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

    [ExternalProcessFact]
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

    [ExternalProcessFact]
    public void GetChangedFilesBetweenRefs_IncludesOldAndNewPathsForRename()
    {
        var repoDir = CreateGitRepo();

        File.WriteAllText(Path.Combine(repoDir, "old.txt"), "v1\n");
        RunGit(repoDir, "add", "old.txt");
        RunGit(repoDir, "commit", "-m", "initial");
        var oldRef = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        File.Move(Path.Combine(repoDir, "old.txt"), Path.Combine(repoDir, "new.txt"));
        File.AppendAllText(Path.Combine(repoDir, "new.txt"), "v2\n");
        RunGit(repoDir, "add", "-A");
        RunGit(repoDir, "commit", "-m", "rename file");

        var changedFiles = GitHelper.GetChangedFilesBetweenRefs(repoDir, oldRef, "HEAD");

        Assert.Contains("old.txt", changedFiles);
        Assert.Contains("new.txt", changedFiles);
    }

    [ExternalProcessFact]
    public void ChangedFileHelpers_NullDelimitedRenamePreservesTabAndNewlinePaths()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = CreateGitRepo();
        const string oldPath = "old\tname.txt";
        const string newPath = "new\nname.txt";
        File.WriteAllText(Path.Combine(repoDir, oldPath), "unchanged rename payload\n");
        RunGit(repoDir, "add", oldPath);
        RunGit(repoDir, "commit", "-m", "initial control path");
        var oldRef = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        File.Move(Path.Combine(repoDir, oldPath), Path.Combine(repoDir, newPath));
        RunGit(repoDir, "add", "-A");
        RunGit(repoDir, "commit", "-m", "rename control path");
        var commitId = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        var commitPaths = GitHelper.GetChangedFilesFromCommit(repoDir, commitId);
        var rangePaths = GitHelper.GetChangedFilesBetweenRefs(repoDir, oldRef, commitId);

        Assert.Contains(oldPath, commitPaths);
        Assert.Contains(newPath, commitPaths);
        Assert.Contains(oldPath, rangePaths);
        Assert.Contains(newPath, rangePaths);
    }

    [ExternalProcessFact]
    public async Task GetChangedFilesFromCommit_DrainsLargeStderrWithoutDeadlock()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatEmitsLargeStderr(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            var task = Task.Run(() => GitHelper.GetChangedFilesFromCommit(repoDir, "0123456789abcdef"));

            var changedFiles = await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["changed.txt"], changedFiles);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void GetChangedFilesFromCommit_UsesTrustedGitExecutableInsteadOfPath_Issue3433()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-trusted-git");
        Directory.CreateDirectory(repoDir);
        var trustedGitDir = Path.Combine(_tempDir, "trusted-git");
        var pathGitDir = Path.Combine(_tempDir, "path-git");
        Directory.CreateDirectory(trustedGitDir);
        Directory.CreateDirectory(pathGitDir);
        WriteFakeGitThatReturnsChangedFile(trustedGitDir, "trusted.txt");
        WriteFakeGitThatReturnsChangedFile(pathGitDir, "path.txt");

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        Environment.SetEnvironmentVariable("PATH", pathGitDir + Path.PathSeparator + oldPath);
        GitHelper.GitExecutablePathOverride = Path.Combine(trustedGitDir, "git");
        try
        {
            var changedFiles = GitHelper.GetChangedFilesFromCommit(repoDir, "0123456789abcdef");

            Assert.Equal(["trusted.txt"], changedFiles);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [ExternalProcessFact]
    public void GetChangedFilesFromCommit_FailsWhenCapturedOutputExceedsLimit()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-output-cap");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatExceedsStdoutLimit(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => GitHelper.GetChangedFilesFromCommit(repoDir, "0123456789abcdef"));

            Assert.Contains("captured stdout exceeded", ex.Message);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void GetChangedFilesFromCommit_FailsWhenNewlineFreeStdoutExceedsLimit_Issue3019()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-output-cap-no-newline");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatExceedsStdoutLimitWithoutNewlines(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => GitHelper.GetChangedFilesFromCommit(repoDir, "0123456789abcdef"));

            Assert.Contains("captured stdout exceeded", ex.Message);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void RunGitCapturingResult_FailsWhenCapturedStderrExceedsLimit_Issue3704()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-stderr-cap");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-stderr-cap");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatExceedsStderrLimit(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            var result = GitHelper.RunGitCapturingResultForTests(
                repoDir,
                gitEnvironmentOverrides: null,
                CancellationToken.None,
                "status");

            Assert.True(
                result.FailureKind == GitCommandFailureKind.CaptureLimitExceeded,
                $"Expected CaptureLimitExceeded, got {result.FailureKind}: {result.Diagnostic ?? result.Error}");
            Assert.Equal(-1, result.ExitCode);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains("captured stderr exceeded", result.Diagnostic);
            Assert.Contains("captured stderr exceeded", result.Error);
            Assert.True(result.Error!.Length <= GitHelper.MaxGitFailureDiagnosticChars);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void GetChangedFilesFromCommit_FailsWhenGitCommandTimesOut()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = CreateGitRepo();
        File.WriteAllText(Path.Combine(repoDir, "tracked.txt"), "tracked\n");
        RunGit(repoDir, "add", "tracked.txt");
        RunGit(repoDir, "commit", "-m", "base");
        var commitId = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        var fakeGitDir = Path.Combine(_tempDir, "fake-git-timeout");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatHangsOnDiffTree(fakeGitDir);
        var fakeGitPidPath = Path.Combine(fakeGitDir, "diff-tree.pid");

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        var oldTimeout = GitHelper.GitCommandTimeout;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        GitHelper.GitCommandTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var ex = Assert.Throws<InvalidOperationException>(
                () => GitHelper.GetChangedFilesFromCommit(repoDir, commitId));
            stopwatch.Stop();

            Assert.True(File.Exists(fakeGitPidPath), "Fake git did not reach diff-tree.");
            var fakeGitPid = int.Parse(
                File.ReadAllText(fakeGitPidPath),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(
                IsProcessRunning(fakeGitPid),
                $"Timed-out fake git process {fakeGitPid} was not reaped.");
            Assert.Contains("timed out", ex.Message);
            Assert.True(
                stopwatch.Elapsed < GitCancellationWallClockLimit,
                $"Commit-diff timeout took {stopwatch.Elapsed}, expected less than {GitCancellationWallClockLimit} before the {FakeGitHangSeconds}-second fake git sleep completed.");
        }
        finally
        {
            GitHelper.GitCommandTimeout = oldTimeout;
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void RunGitCapturingResult_NonZeroExitReportsStructuredBoundedDiagnostic_Issue3434()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-git-structured-failure");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-structured-failure");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatFailsWithLongSensitiveStderr(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            var result = GitHelper.RunGitCapturingResultForTests(
                repoDir,
                gitEnvironmentOverrides: null,
                CancellationToken.None,
                "status");

            Assert.Equal(23, result.ExitCode);
            Assert.Equal(GitCommandFailureKind.ExitCode, result.FailureKind);
            Assert.Equal(result.Diagnostic, result.Error);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains("[redacted]", result.Diagnostic);
            Assert.Contains("truncated", result.Diagnostic);
            Assert.True(result.Diagnostic!.Length < 700, result.Diagnostic);
            Assert.DoesNotContain("/Users/example/private", result.Diagnostic);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void RunGitCapturingResult_TimeoutReportsStructuredFailure_Issue3434()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-git-structured-timeout");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-structured-timeout");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatHangsForAnyCommand(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        var oldTimeout = GitHelper.GitCommandTimeout;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        GitHelper.GitCommandTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = GitHelper.RunGitCapturingResultForTests(
                repoDir,
                gitEnvironmentOverrides: null,
                CancellationToken.None,
                "status");
            stopwatch.Stop();

            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(GitCommandFailureKind.TimedOut, result.FailureKind);
            Assert.Contains("timed out", result.Diagnostic);
            Assert.True(
                stopwatch.Elapsed < GitCancellationWallClockLimit,
                $"Timeout took {stopwatch.Elapsed}, expected less than {GitCancellationWallClockLimit}.");
        }
        finally
        {
            GitHelper.GitCommandTimeout = oldTimeout;
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void RunGitCapturingResult_CallerCancellationStopsProcessWait_Issue3969()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-git-caller-cancel");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-caller-cancel");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatHangsForAnyCommand(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        var oldTimeout = GitHelper.GitCommandTimeout;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        GitHelper.GitCommandTimeout = TimeSpan.FromSeconds(5);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var stopwatch = Stopwatch.StartNew();

            var ex = Assert.Throws<OperationCanceledException>(() =>
                GitHelper.RunGitCapturingResultForTests(
                    repoDir,
                    gitEnvironmentOverrides: null,
                    cts.Token,
                    "status"));

            stopwatch.Stop();
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.True(
                stopwatch.Elapsed < GitCancellationWallClockLimit,
                $"Caller cancellation took {stopwatch.Elapsed}, expected less than {GitCancellationWallClockLimit}.");
        }
        finally
        {
            GitHelper.GitCommandTimeout = oldTimeout;
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void RunGitCapturingResult_StartFailureReportsStructuredRedactedDiagnostic_Issue3434()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-git-start-failure");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-start-failure");
        Directory.CreateDirectory(fakeGitDir);
        var fakeGitPath = WriteNonExecutableFakeGit(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = fakeGitPath;
        try
        {
            var result = GitHelper.RunGitCapturingResultForTests(
                repoDir,
                gitEnvironmentOverrides: null,
                CancellationToken.None,
                "status");

            Assert.Null(result.ExitCode);
            Assert.Equal(GitCommandFailureKind.StartFailed, result.FailureKind);
            Assert.NotNull(result.Diagnostic);
            Assert.DoesNotContain(fakeGitDir, result.Diagnostic);
            Assert.True(result.Diagnostic!.Length < 700, result.Diagnostic);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void RunGitCapturingResult_ScrubsInheritedEnvironmentByAllowlist_Issue3910()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var env = EnvironmentVariableScope.Capture(
            "HTTPS_PROXY",
            "CDIDX_TEST_GIT_POLICY_3910",
            "CDIDX_SECRET_GIT_POLICY_3910");
        env.Set("HTTPS_PROXY", "http://proxy.example.test:8080");
        env.Set("CDIDX_TEST_GIT_POLICY_3910", "test-only");
        env.Set("CDIDX_SECRET_GIT_POLICY_3910", "secret");

        var repoDir = Path.Combine(_tempDir, "repo-git-env");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-env");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatPrintsSelectedEnvironment(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            var result = GitHelper.RunGitCapturingResultForTests(
                repoDir,
                gitEnvironmentOverrides: null,
                CancellationToken.None,
                "status");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("HTTPS_PROXY=http://proxy.example.test:8080", result.Output);
            Assert.Contains("GIT_TERMINAL_PROMPT=0", result.Output);
            Assert.Contains("CDIDX_TEST_GIT_POLICY_3910=", result.Output);
            Assert.Contains("CDIDX_SECRET_GIT_POLICY_3910=", result.Output);
            Assert.DoesNotContain("test-only", result.Output);
            Assert.DoesNotContain("secret", result.Output);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void GetChangedFilesFromCommit_CancelDuringGitCommand_ThrowsOperationCanceled()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-cancel");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-cancel");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatHangsOnDiffTree(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            var stopwatch = Stopwatch.StartNew();

            var ex = Assert.Throws<OperationCanceledException>(
                () => GitHelper.GetChangedFilesFromCommit(repoDir, "0123456789abcdef", cts.Token));

            stopwatch.Stop();
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.True(
                stopwatch.Elapsed < GitCancellationWallClockLimit,
                $"Cancellation took {stopwatch.Elapsed}, expected less than {GitCancellationWallClockLimit}.");
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void ResolveIgnoreCase_CancelDuringGitCommand_ThrowsOperationCanceled()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-ignorecase-cancel");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-ignorecase-cancel");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatHangsOnRevParse(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            var stopwatch = Stopwatch.StartNew();

            var ex = Assert.Throws<OperationCanceledException>(
                () => GitHelper.ResolveIgnoreCase(repoDir, cts.Token));

            stopwatch.Stop();
            Assert.Equal(cts.Token, ex.CancellationToken);
            Assert.True(
                stopwatch.Elapsed < GitCancellationWallClockLimit,
                $"Cancellation took {stopwatch.Elapsed}, expected less than {GitCancellationWallClockLimit}.");
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void RunGitCapturingResult_CancelDuringOutputCapture_ThrowsOperationCanceled_Issue3761()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-output-cancel");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-output-cancel");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatStreamsStdoutUntilKilled(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var stopwatch = Stopwatch.StartNew();

            Assert.Throws<OperationCanceledException>(() =>
                GitHelper.RunGitCapturingResultForTests(
                    repoDir,
                    gitEnvironmentOverrides: null,
                    cts.Token,
                    "status"));

            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed < GitCancellationWallClockLimit,
                $"Cancellation during output capture took {stopwatch.Elapsed}, expected less than {GitCancellationWallClockLimit}.");
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [Fact]
    public void TrustedGitExecutableCandidates_OnMacOS_ExcludeDeveloperToolsShim_Issue3433()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var candidates = GitHelper.TrustedGitExecutableCandidatePathsForTests();

        Assert.DoesNotContain("/usr/bin/git", candidates);
        Assert.Contains("/Library/Developer/CommandLineTools/usr/bin/git", candidates);
        Assert.Contains("/Applications/Xcode.app/Contents/Developer/usr/bin/git", candidates);
    }

    [ExternalProcessTheory]
    [InlineData("nix/store/hash-git-2.0/bin")]
    [InlineData("custom-prefix/bin")]
    [InlineData("portable/bin")]
    public void GitExecutableEnvironmentOverride_AcceptsNixCustomPrefixAndPortableAbsoluteGit_Issue4599(string relativePrefix)
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "portable-git-repo");
        var portableRoot = Path.Combine(
            Path.DirectorySeparatorChar.ToString(),
            "tmp",
            $"cdidx_issue4599_{Guid.NewGuid():N}");
        var portableGitDir = Path.Combine(portableRoot, relativePrefix);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(portableGitDir);
        WriteFakeGitThatReturnsChangedFile(portableGitDir, "portable.txt");
        var portableGitPath = Path.Combine(portableGitDir, "git");

        using var env = EnvironmentVariableScope.Capture(GitHelper.GitExecutableEnvironmentVariable);
        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = null;
        env.Set(GitHelper.GitExecutableEnvironmentVariable, portableGitPath);
        try
        {
            var changedFiles = GitHelper.GetChangedFilesFromCommit(repoDir, "0123456789abcdef");
            var status = GitHelper.GetGitExecutableStatus();
            var trustOverride = Assert.Single(GitHelper.GetAcceptedTrustOverrides(status));

            Assert.Equal(["portable.txt"], changedFiles);
            Assert.Equal("environment_override", status.Source);
            Assert.True(status.Accepted);
            Assert.Equal("accepted", status.Reason);
            Assert.Equal("git", status.Path);
            Assert.True(status.OwnerOnlyWritable);
            Assert.Equal("0700", status.UnixMode);
            Assert.True(status.Executable);
            Assert.Equal("current_user", status.Owner);
            Assert.True(status.OwnerTrusted);
            Assert.True(status.AncestorDirectoriesTrusted);
            Assert.Equal(GitHelper.GitExecutableEnvironmentVariable, trustOverride.EnvironmentVariable);
            Assert.Contains("mode 0700", trustOverride.Message, StringComparison.Ordinal);
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
            TestProjectHelper.DeleteDirectory(portableRoot);
        }
    }

    [ExternalProcessFact]
    public void GitExecutableEnvironmentOverride_ReportsUnsafeModeAndMissingExecuteBit_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var portableGitDir = Path.Combine(_tempDir, "portable-git-diagnostics");
        Directory.CreateDirectory(portableGitDir);
        var portableGitPath = Path.Combine(portableGitDir, "git");
        File.WriteAllText(portableGitPath, "#!/bin/sh\nexit 0\n");

        using var env = EnvironmentVariableScope.Capture(GitHelper.GitExecutableEnvironmentVariable);
        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = null;
        env.Set(GitHelper.GitExecutableEnvironmentVariable, portableGitPath);
        try
        {
            File.SetUnixFileMode(
                portableGitPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
            var sharedWritable = GitHelper.GetGitExecutableStatus();

            Assert.False(sharedWritable.Accepted);
            Assert.Equal("shared_writable", sharedWritable.Reason);
            Assert.False(sharedWritable.OwnerOnlyWritable);
            Assert.Null(sharedWritable.Executable);
            Assert.Empty(GitHelper.GetAcceptedTrustOverrides(sharedWritable));

            File.SetUnixFileMode(portableGitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var nonExecutable = GitHelper.GetGitExecutableStatus();

            Assert.False(nonExecutable.Accepted);
            Assert.Equal("not_executable", nonExecutable.Reason);
            Assert.True(nonExecutable.OwnerOnlyWritable);
            Assert.False(nonExecutable.Executable);

            env.Set(GitHelper.GitExecutableEnvironmentVariable, "git");
            var relativePath = GitHelper.GetGitExecutableStatus();

            Assert.False(relativePath.Accepted);
            Assert.Equal("path_not_absolute", relativePath.Reason);
            Assert.Null(relativePath.Path);

            env.Set(GitHelper.GitExecutableEnvironmentVariable, portableGitPath + ".sh");
            var unexpectedName = GitHelper.GetGitExecutableStatus();

            Assert.False(unexpectedName.Accepted);
            Assert.Equal("unexpected_filename", unexpectedName.Reason);

            env.Set(GitHelper.GitExecutableEnvironmentVariable, portableGitPath);
            File.SetUnixFileMode(
                portableGitPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var wrongIdentity = GitHelper.GetGitExecutableStatus();

            Assert.False(wrongIdentity.Accepted);
            Assert.Equal("execution_probe_failed", wrongIdentity.Reason);
            Assert.False(wrongIdentity.Executable);
            Assert.Equal("current_user", wrongIdentity.Owner);
            Assert.True(wrongIdentity.OwnerTrusted);
            Assert.True(wrongIdentity.AncestorDirectoriesTrusted);

            var originalDirectoryMode = File.GetUnixFileMode(portableGitDir);
            try
            {
                File.SetUnixFileMode(portableGitDir, originalDirectoryMode | UnixFileMode.GroupWrite);
                var unsafeAncestor = GitHelper.GetGitExecutableStatus();

                Assert.False(unsafeAncestor.Accepted);
                Assert.Equal("ancestor_untrusted", unsafeAncestor.Reason);
                Assert.False(unsafeAncestor.AncestorDirectoriesTrusted);
            }
            finally
            {
                File.SetUnixFileMode(portableGitDir, originalDirectoryMode);
            }
        }
        finally
        {
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [Fact]
    public void GitExecutableTrustOverride_UsesSingleStatusSnapshot_Issue4599()
    {
        if (OperatingSystem.IsWindows())
            return;

        var portableGitDir = Path.Combine(_tempDir, "single-status-snapshot");
        Directory.CreateDirectory(portableGitDir);
        var portableGitPath = Path.Combine(portableGitDir, "git");
        File.WriteAllText(portableGitPath, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(
            portableGitPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var env = EnvironmentVariableScope.Capture(GitHelper.GitExecutableEnvironmentVariable);
        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        var oldProbe = GitHelper.GitVersionProbeForTesting;
        var probeCount = 0;
        GitHelper.GitExecutablePathOverride = null;
        GitHelper.GitVersionProbeForTesting = _ => ++probeCount == 1;
        env.Set(GitHelper.GitExecutableEnvironmentVariable, portableGitPath);
        try
        {
            var status = GitHelper.GetGitExecutableStatus();
            var trustOverride = Assert.Single(GitHelper.GetAcceptedTrustOverrides(status));

            Assert.True(status.Accepted);
            Assert.Equal(1, probeCount);
            Assert.Equal(GitHelper.GitExecutableEnvironmentVariable, trustOverride.EnvironmentVariable);
        }
        finally
        {
            GitHelper.GitVersionProbeForTesting = oldProbe;
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [Fact]
    public void GitExecutableEnvironmentOverride_WindowsRequiresTrustedAclBeforeProbe_Issue4599()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var trustedGit = Assert.Single(
            GitHelper.TrustedGitExecutableCandidatePathsForTests()
                .Where(File.Exists)
                .Take(1));
        var portableGitDir = TestProjectHelper.CreateTrustedWindowsGitDirectory("cdidx_windows_portable_git");
        var portableGitPath = Path.Combine(portableGitDir, "git.exe");
        File.Copy(trustedGit, portableGitPath);

        using var env = EnvironmentVariableScope.Capture(GitHelper.GitExecutableEnvironmentVariable);
        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        var oldProbe = GitHelper.GitVersionProbeForTesting;
        var probeCount = 0;
        GitHelper.GitExecutablePathOverride = null;
        GitHelper.GitVersionProbeForTesting = _ =>
        {
            probeCount++;
            return true;
        };
        env.Set(GitHelper.GitExecutableEnvironmentVariable, portableGitPath);
        try
        {
            var accepted = GitHelper.GetGitExecutableStatus();

            Assert.True(accepted.Accepted);
            Assert.True(accepted.OwnerTrusted);
            Assert.True(accepted.AncestorDirectoriesTrusted);
            Assert.Equal(1, probeCount);

            var fileInfo = new FileInfo(portableGitPath);
            var security = FileSystemAclExtensions.GetAccessControl(fileInfo);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
                FileSystemRights.WriteData,
                AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(fileInfo, security);
            probeCount = 0;

            var rejected = GitHelper.GetGitExecutableStatus();

            Assert.False(rejected.Accepted);
            Assert.Equal("acl_untrusted", rejected.Reason);
            Assert.Equal(0, probeCount);
        }
        finally
        {
            GitHelper.GitVersionProbeForTesting = oldProbe;
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
            TestProjectHelper.DeleteDirectory(portableGitDir);
        }
    }

    [ExternalProcessFact]
    public void TryGetHeadCommitResult_OnMacOS_DoesNotUseDeveloperDirShimGit_Issue3433()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var projectDir = Path.Combine(_tempDir, "developer-dir-project");
        Directory.CreateDirectory(projectDir);
        var developerDir = Path.Combine(_tempDir, "FakeDeveloper");
        var developerGitDir = Path.Combine(developerDir, "usr", "bin");
        Directory.CreateDirectory(developerGitDir);
        var markerPath = Path.Combine(_tempDir, "developer-dir-git-ran.txt");
        var fakeGitPath = Path.Combine(developerGitDir, "git");
        File.WriteAllText(fakeGitPath, $"""
#!/bin/sh
printf ran > "{markerPath.Replace("\"", "\\\"", StringComparison.Ordinal)}"
exit 7
""");
        File.SetUnixFileMode(fakeGitPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var env = EnvironmentVariableScope.Capture("DEVELOPER_DIR");
        env.Set("DEVELOPER_DIR", developerDir);

        _ = GitHelper.TryGetHeadCommitResult(projectDir);

        Assert.False(File.Exists(markerPath), "GitHelper must not execute git selected through DEVELOPER_DIR.");
    }

    [ExternalProcessTheory]
    [InlineData("feature")]
    [InlineData("v1.0.0")]
    [InlineData("main..feature")]
    public void GetChangedFilesFromCommit_RejectsNonCommitIdRefs(string commitRef)
    {
        var repoDir = CreateGitRepo();

        var ex = Assert.Throws<ArgumentException>(() => GitHelper.GetChangedFilesFromCommit(repoDir, commitRef));

        Assert.Contains("Invalid commit ID", ex.Message);
    }

    [ExternalProcessFact]
    public void HeadMetadataLifecycle_UnbornResolvedSubdirectoryAndDetached_ReturnsConsistentValues()
    {
        var repoDir = CreateGitRepo();

        AssertHeadResult(GitHelper.TryGetHeadCommitResult(repoDir), GitHeadCommitState.None, expectedSha: null);

        RunGit(repoDir, "commit", "--allow-empty", "-m", "initial");
        RunGit(repoDir, "branch", "-M", "cdidx-head-lifecycle");
        var expected = RunGit(repoDir, "rev-parse", "HEAD").Trim();
        var projectDir = Path.Combine(repoDir, "src", "App");
        Directory.CreateDirectory(projectDir);

        Assert.Equal(expected, GitHelper.TryGetHeadCommit(repoDir));
        AssertHeadResult(GitHelper.TryGetHeadCommitResult(repoDir), GitHeadCommitState.Resolved, expected);
        AssertHeadResult(GitHelper.TryGetHeadCommitResult(projectDir), GitHeadCommitState.Resolved, expected);
        Assert.Equal("cdidx-head-lifecycle", GitHelper.TryGetHeadBranch(repoDir));

        RunGit(repoDir, "checkout", "--detach", expected);
        AssertHeadResult(GitHelper.TryGetHeadCommitResult(repoDir), GitHeadCommitState.DetachedHead, expected);
        Assert.Null(GitHelper.TryGetHeadBranch(repoDir));
    }

    [ExternalProcessFact]
    public void TryGetHeadCommitResult_ReturnsNotARepo()
    {
        var nonRepo = Path.Combine(_tempDir, "not-a-repo");
        Directory.CreateDirectory(nonRepo);

        var actual = GitHelper.TryGetHeadCommitResult(nonRepo);

        Assert.Equal(GitHeadCommitState.NotARepo, actual.State);
        Assert.Null(actual.Sha);
        Assert.Null(actual.Reason);
    }

    [ExternalProcessFact]
    public void TryGetHeadCommitResult_RootDiscoveryTimeoutReportsStructuredFailure_Issue3434()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-head-root-timeout");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-head-root-timeout");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatHangsForAnyCommand(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        var oldTimeout = GitHelper.GitCommandTimeout;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        GitHelper.GitCommandTimeout = TimeSpan.FromMilliseconds(100);
        try
        {
            var actual = GitHelper.TryGetHeadCommitResult(repoDir);

            Assert.Equal(GitHeadCommitState.Error, actual.State);
            Assert.Null(actual.Sha);
            Assert.Equal(GitCommandFailureKind.TimedOut, actual.FailureKind);
            Assert.Contains("timed out", actual.Diagnostic);
            Assert.NotEqual(GitHeadCommitState.NotARepo, actual.State);
        }
        finally
        {
            GitHelper.GitCommandTimeout = oldTimeout;
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }
    }

    [ExternalProcessFact]
    public void TryResolveCommit_CanceledTokenStopsGitProcess_Issue3723()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repoDir = Path.Combine(_tempDir, "repo-resolve-cancel");
        Directory.CreateDirectory(repoDir);
        var fakeGitDir = Path.Combine(_tempDir, "fake-git-resolve-cancel");
        Directory.CreateDirectory(fakeGitDir);
        WriteFakeGitThatHangsForAnyCommand(fakeGitDir);

        var oldGitExecutablePath = GitHelper.GitExecutablePathOverride;
        GitHelper.GitExecutablePathOverride = Path.Combine(fakeGitDir, "git");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Assert.Throws<OperationCanceledException>(
                () => GitHelper.TryResolveCommit(repoDir, "HEAD", cts.Token));
        }
        finally
        {
            stopwatch.Stop();
            GitHelper.GitExecutablePathOverride = oldGitExecutablePath;
        }

        Assert.True(
            stopwatch.Elapsed < GitCancellationWallClockLimit,
            $"git cancellation should stop before the fake git sleep completes; elapsed={stopwatch.Elapsed}");
    }

    [ExternalProcessFact]
    public void TryGetHeadCommitResult_ReturnsErrorForCorruptGitDirectory()
    {
        var repoDir = Path.Combine(_tempDir, "corrupt-repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));

        var actual = GitHelper.TryGetHeadCommitResult(repoDir);

        Assert.Equal(GitHeadCommitState.Error, actual.State);
        Assert.Null(actual.Sha);
        Assert.False(string.IsNullOrWhiteSpace(actual.Reason));
    }

    [ExternalProcessFact]
    public void TryGetHeadCommitResult_ReturnsResolvedForBareRepository()
    {
        var sourceRepo = CreateGitRepo();
        File.WriteAllText(Path.Combine(sourceRepo, "tracked.txt"), "v1\n");
        RunGit(sourceRepo, "add", "tracked.txt");
        RunGit(sourceRepo, "commit", "-m", "initial");
        var bareRepo = Path.Combine(_tempDir, "head-result.git");
        RunGit(_tempDir, "clone", "--bare", sourceRepo, bareRepo);

        var expected = RunGit(bareRepo, "rev-parse", "HEAD").Trim();
        var actual = GitHelper.TryGetHeadCommitResult(bareRepo);

        Assert.Equal(GitHeadCommitState.Resolved, actual.State);
        Assert.Equal(expected, actual.Sha);
        Assert.Null(actual.Reason);
    }

    [ExternalProcessFact]
    public void TryCountCommitsAhead_EqualLinearDivergentAndInvalidBases_ReturnsExpectedCounts()
    {
        var repoDir = CreateGitRepo();
        RunGit(repoDir, "commit", "--allow-empty", "-m", "base");
        RunGit(repoDir, "branch", "-M", "cdidx-ahead-main");
        var indexedSha = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        Assert.Equal(0, GitHelper.TryCountCommitsAhead(repoDir, indexedSha));

        RunGit(repoDir, "switch", "-c", "cdidx-ahead-divergent");
        RunGit(repoDir, "commit", "--allow-empty", "-m", "divergent");
        var divergentSha = RunGit(repoDir, "rev-parse", "HEAD").Trim();

        RunGit(repoDir, "switch", "cdidx-ahead-main");
        RunGit(repoDir, "commit", "--allow-empty", "-m", "second");
        RunGit(repoDir, "commit", "--allow-empty", "-m", "third");

        Assert.Equal(2, GitHelper.TryCountCommitsAhead(repoDir, indexedSha));
        Assert.Null(GitHelper.TryCountCommitsAhead(repoDir, divergentSha));
        Assert.Null(GitHelper.TryCountCommitsAhead(repoDir, "--upload-pack=evil"));
        Assert.Null(GitHelper.TryCountCommitsAhead(repoDir, string.Empty));
    }

    [ExternalProcessFact]
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

    [ExternalProcessFact]
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

    [ExternalProcessFact]
    public void ResolveIgnoreCase_RootAndSubdirectoryAcrossConfigChanges_ReturnsConfiguredValue()
    {
        var repoDir = CreateInitializedGitRepo();
        var subDir = Path.Combine(repoDir, "src", "module");
        Directory.CreateDirectory(subDir);

        RunGit(repoDir, "config", "core.ignorecase", "true");

        Assert.True(GitHelper.ResolveIgnoreCase(repoDir));
        Assert.True(GitHelper.ResolveIgnoreCase(subDir));

        RunGit(repoDir, "config", "core.ignorecase", "false");

        Assert.False(GitHelper.ResolveIgnoreCase(repoDir));
        Assert.False(GitHelper.ResolveIgnoreCase(subDir));
    }

    [ExternalProcessFact]
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

    [ExternalProcessFact]
    public void ResolveIgnoreCase_NonRepoProbeAvoidsRootProbeArtifacts_Issue3174()
    {
        var nonRepoDir = Path.Combine(_tempDir, $"non_repo_probe_{Guid.NewGuid():N}");
        var fakeHome = Path.Combine(_tempDir, $"fake_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(nonRepoDir);
        Directory.CreateDirectory(fakeHome);
        var environment = new Dictionary<string, string?>
        {
            ["HOME"] = fakeHome,
            ["XDG_CONFIG_HOME"] = Path.Combine(fakeHome, ".config"),
            ["GIT_CONFIG_NOSYSTEM"] = "1",
        };

        var resolved = GitHelper.ResolveIgnoreCase(nonRepoDir, environment);
        var expected = ProbeDirectoryIgnoreCaseLikeProduction(nonRepoDir);

        Assert.Equal(expected, resolved);
        Assert.Empty(Directory.GetFiles(nonRepoDir, ".cdidx_case_probe_*", SearchOption.TopDirectoryOnly));
        Assert.False(Directory.Exists(Path.Combine(nonRepoDir, CaseSensitivityProbeDirectory.DataDirectoryName)));
    }

    [ExternalProcessFact]
    public void ResolveIgnoreCase_ProbeFailureThrowsStructuredFilesystemError_Issue3439()
    {
        var nonRepoDir = Path.Combine(_tempDir, $"non_repo_probe_failure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(nonRepoDir);
        var previousProbe = GitHelper.FileSystemIgnoreCaseProbeForTesting;
        GitHelper.FileSystemIgnoreCaseProbeForTesting = _ => throw new IOException("probe blocked");
        try
        {
            var ex = Assert.Throws<CodeIndexException>(() => GitHelper.ResolveIgnoreCase(nonRepoDir));

            Assert.Equal(CommandErrorCodes.FileSystemCaseProbeFailed, ex.Code);
            Assert.Equal(CodeIndexExceptionCategory.Filesystem, ex.Category);
            Assert.Equal(Path.GetFullPath(nonRepoDir), ex.Path);
            Assert.IsType<IOException>(ex.InnerException);
        }
        finally
        {
            GitHelper.FileSystemIgnoreCaseProbeForTesting = previousProbe;
        }
    }

    private string CreateGitRepo()
    {
        var repoDir = CreateInitializedGitRepo();

        RunGit(repoDir, "config", "user.name", "CodeIndex Tests");
        RunGit(repoDir, "config", "user.email", "tests@example.com");
        RunGit(repoDir, "config", "commit.gpgsign", "false");
        RunGit(repoDir, "config", "tag.gpgsign", "false");

        return repoDir;
    }

    private string CreateInitializedGitRepo()
    {
        var repoDir = Path.Combine(_tempDir, $"repo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);

        RunGit(repoDir, "init");

        return repoDir;
    }

    private static void AssertHeadResult(
        GitHeadCommitResult actual,
        GitHeadCommitState expectedState,
        string? expectedSha)
    {
        Assert.Equal(expectedState, actual.State);
        Assert.Equal(expectedSha, actual.Sha);
        Assert.Null(actual.Reason);
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
    commit=${3%^\{commit\}}
    printf '%s\n' "$commit"
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

    private static void WriteFakeGitThatReturnsChangedFile(string directory, string changedPath)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, """
#!/bin/sh
if [ "$1" = "--version" ]; then
  printf '%s\n' 'git version 2.0.0'
  exit 0
fi
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
  printf 'M\t__CHANGED_PATH__\n'
  exit 0
fi
exit 1
""".Replace("__CHANGED_PATH__", changedPath, StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteFakeGitThatPrintsSelectedEnvironment(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, """
#!/bin/sh
printf 'HTTPS_PROXY=%s\n' "${HTTPS_PROXY:-}"
printf 'GIT_TERMINAL_PROMPT=%s\n' "${GIT_TERMINAL_PROMPT:-}"
printf 'CDIDX_TEST_GIT_POLICY_3910=%s\n' "${CDIDX_TEST_GIT_POLICY_3910:-}"
printf 'CDIDX_SECRET_GIT_POLICY_3910=%s\n' "${CDIDX_SECRET_GIT_POLICY_3910:-}"
exit 0
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteFakeGitThatExceedsStdoutLimit(string directory)
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
  perl -e 'for ($i = 0; $i < 80000; $i++) { print "M\tchanged_$i.txt\n" }'
  exit 0
fi
exit 1
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteFakeGitThatExceedsStdoutLimitWithoutNewlines(string directory)
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
  perl -e 'print "x" x 1048577'
  exit 0
fi
exit 1
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteFakeGitThatExceedsStderrLimit(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, """
#!/bin/sh
perl -e 'print STDERR "e" x 1048577'
exit 0
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteFakeGitThatHangsOnDiffTree(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, $$"""
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
  printf '%s\n' "$$" > "$(dirname "$0")/diff-tree.pid"
  sleep {{FakeGitHangSeconds}}
  exit 0
fi
exit 1
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void WriteFakeGitThatFailsWithLongSensitiveStderr(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, """
#!/bin/sh
perl -e 'print STDERR "/Users/example/private/repo/.git/config " . ("x" x 2000)'
exit 23
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteFakeGitThatHangsForAnyCommand(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, $$"""
#!/bin/sh
sleep {{FakeGitHangSeconds}}
exit 0
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string WriteNonExecutableFakeGit(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return script;
    }

    private static void WriteFakeGitThatHangsOnRevParse(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, """
#!/bin/sh
if [ "$1" = "rev-parse" ]; then
  sleep 5
  exit 0
fi
exit 1
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteFakeGitThatStreamsStdoutUntilKilled(string directory)
    {
        var script = Path.Combine(directory, "git");
        File.WriteAllText(script, """
#!/bin/sh
perl -e '$|=1; while (1) { print "x" x 4096; select undef, undef, undef, 0.01 }'
""");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool ProbeDirectoryIgnoreCaseLikeProduction(string path)
    {
        using var probe = CaseSensitivityProbeDirectory.CreateProbePathScope(path, "case-probe-test-");
        var probePath = probe.Path;
        File.WriteAllText(probePath, string.Empty);
        try
        {
            return TryCreateCaseVariant(probePath, out var probeVariant) && File.Exists(probeVariant);
        }
        finally
        {
            TestProjectHelper.DeleteFile(probePath);
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

}
