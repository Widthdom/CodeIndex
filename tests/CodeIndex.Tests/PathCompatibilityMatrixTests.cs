using System.Runtime.InteropServices;
using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class PathCompatibilityMatrixTests
{
    public static TheoryData<string, bool, string, string[], bool> PathBoundaryCases()
    {
        var data = new TheoryData<string, bool, string, string[], bool>();
        data.Add("case-insensitive descendants match", true, "Project", ["project", "src", "App.cs"], true);
        data.Add("case-sensitive descendants keep case variants distinct", false, "Project", ["project", "src", "App.cs"], false);
        data.Add("case-insensitive prefix collisions stay outside", true, "Project", ["ProjectExtras", "App.cs"], false);
        data.Add("case-sensitive prefix collisions stay outside", false, "Project", ["ProjectExtras", "App.cs"], false);
        return data;
    }

    [Theory]
    [MemberData(nameof(PathBoundaryCases))]
    public void PathBoundaryMatrix_UsesSeededFilesystemCasePolicy(
        string scenario,
        bool ignoreCase,
        string parentSegment,
        string[] childSegments,
        bool expected)
    {
        _ = scenario;
        using var workspace = MatrixWorkspace.Create("cdidx_path_matrix");
        PathCasing.ResetCacheForTests();
        try
        {
            PathCasing.SeedFromWorkspace(workspace.Root, ignoreCase);

            Assert.Equal(expected, PathCasing.IsFullPathEqualOrParent(
                workspace.FullPath(parentSegment),
                workspace.FullPath(childSegments)));
        }
        finally
        {
            PathCasing.ResetCacheForTests();
        }
    }

    public static TheoryData<string, string, string, string> LongPathCases()
    {
        var data = new TheoryData<string, string, string, string>();
        var longDrive = @"C:\repo\" + new string('a', LongPath.LongPathThreshold);
        var longUnc = @"\\server\share\" + new string('b', LongPath.LongPathThreshold);
        data.Add("long drive path", longDrive, @"\\?\" + longDrive, longDrive);
        data.Add("long UNC path", longUnc, @"\\?\UNC\server\share\" + new string('b', LongPath.LongPathThreshold), longUnc);
        data.Add("device namespace path", @"\\.\PhysicalDrive0\" + new string('c', LongPath.LongPathThreshold), @"\\.\PhysicalDrive0\" + new string('c', LongPath.LongPathThreshold), @"\\.\PhysicalDrive0\" + new string('c', LongPath.LongPathThreshold));
        data.Add("relative path", @"src\" + new string('d', LongPath.LongPathThreshold), @"src\" + new string('d', LongPath.LongPathThreshold), @"src\" + new string('d', LongPath.LongPathThreshold));
        data.Add("POSIX absolute path", "/repo/" + new string('e', LongPath.LongPathThreshold), "/repo/" + new string('e', LongPath.LongPathThreshold), "/repo/" + new string('e', LongPath.LongPathThreshold));
        return data;
    }

    [Theory]
    [MemberData(nameof(LongPathCases))]
    public void LongPathMatrix_WindowsPrefixRoundTripsOnlyWindowsAbsoluteForms(
        string scenario,
        string input,
        string expectedPrefixed,
        string expectedRoundTrip)
    {
        _ = scenario;

        var prefixed = LongPath.EnsureWindowsPrefixCore(input, isWindows: true);

        Assert.Equal(expectedPrefixed, prefixed);
        Assert.Equal(expectedRoundTrip, LongPath.RemoveWindowsPrefixCore(prefixed, isWindows: true));
        Assert.Equal(input, LongPath.EnsureWindowsPrefixCore(input, isWindows: false));
    }

    [Fact]
    public void PosixPermissionMatrix_HardensSensitiveDirectoriesAndFiles()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        using var workspace = MatrixWorkspace.Create("cdidx_permission_matrix");
        var stateFile = workspace.FullPath("state", "cache.json");

        DataDirectorySecurity.CreateSensitiveParentDirectoryForFile(stateFile);
        DataDirectorySecurity.WritePrivateText(stateFile, "{}");

        Assert.Equal(
            DataDirectorySecurity.PrivateDirectoryMode,
            File.GetUnixFileMode(Path.GetDirectoryName(stateFile)!) & DataDirectorySecurity.PermissionBits);
        Assert.Equal(
            DataDirectorySecurity.PrivateFileMode,
            File.GetUnixFileMode(stateFile) & DataDirectorySecurity.PermissionBits);
    }

    [Fact]
    public void ScannerMatrix_RecordsSubmodulePassthroughAndDanglingSymlinks()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var workspace = MatrixWorkspace.Create("cdidx_scanner_matrix");
        workspace.WriteText("app.py", "print('app')\n");
        workspace.WriteText(
            ".gitmodules",
            "[submodule \"foo\"]\n\tpath = vendor/foo\n\turl = https://example.invalid/foo.git\n");
        workspace.WriteText("vendor/vendor_dep.py", "x = 1\n");
        workspace.WriteText("vendor/foo/.git", "gitdir: ../../.git/modules/foo\n");
        workspace.WriteText("vendor/foo/lib.py", "def f(): pass\n");
        Directory.CreateSymbolicLink(
            workspace.FullPath("missing-link"),
            workspace.FullPath("missing-target"));

        var result = new FileIndexer(workspace.Root, ignoreCase: false).ScanFilesDetailed();
        var relativeFiles = workspace.RelativeFiles(result);

        Assert.Contains("app.py", relativeFiles);
        Assert.Contains("vendor/foo/lib.py", relativeFiles);
        Assert.DoesNotContain("vendor/vendor_dep.py", relativeFiles);
        Assert.Contains("missing-link", result.DanglingSymlinks);
        Assert.Contains(
            result.Errors,
            error => error.Path == "missing-link"
                && error.Severity == FileIndexer.ScanIssueSeverity.Warning
                && error.Message.Contains("dangling symlink", StringComparison.OrdinalIgnoreCase));
        Assert.False(result.HadErrors);
    }

    [Fact]
    public void GitSkipWorktreeMatrix_ReturnsNormalizedSparsePaths()
    {
        using var workspace = MatrixWorkspace.Create("cdidx_skip_worktree_matrix");
        TestProjectHelper.InitializeGitRepo(workspace.Root);
        workspace.WriteText("src/App.cs", "class App { }\n");
        workspace.WriteText("generated/Skip.cs", "class Skip { }\n");
        TestProjectHelper.RunGit(workspace.Root, "add", "src/App.cs", "generated/Skip.cs");
        TestProjectHelper.RunGit(workspace.Root, "commit", "-m", "seed");

        TestProjectHelper.RunGit(workspace.Root, "update-index", "--skip-worktree", "generated/Skip.cs");

        var skipWorktreePaths = GitHelper.TryGetSkipWorktreePaths(workspace.Root);

        Assert.NotNull(skipWorktreePaths);
        Assert.Contains("generated/Skip.cs", skipWorktreePaths);
        Assert.DoesNotContain(skipWorktreePaths!, path => path.Contains('\\', StringComparison.Ordinal));
    }

    private sealed class MatrixWorkspace : IDisposable
    {
        private MatrixWorkspace(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public static MatrixWorkspace Create(string prefix)
            => new(TestProjectHelper.CreateTempProject(prefix));

        public string FullPath(params string[] segments)
            => TestProjectHelper.ProjectPath(Root, segments);

        public string WriteText(string relativePath, string content)
            => TestProjectHelper.WriteTextFile(Root, relativePath, content);

        public HashSet<string> RelativeFiles(FileIndexer.ScanFilesResult result)
            => result.Files
                .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
                .ToHashSet(StringComparer.Ordinal);

        public void Dispose()
        {
            TestProjectHelper.DeleteDirectory(Root);
        }
    }
}
