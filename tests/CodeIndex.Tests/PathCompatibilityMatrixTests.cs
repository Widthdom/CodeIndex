using System.Runtime.InteropServices;
using CodeIndex.Cli;
using CodeIndex.Indexer;

namespace CodeIndex.Tests;

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
        lock (PathCasingTestLock.Gate)
        {
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
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void FileNameLanguageMatrix_UsesSeededFilesystemCasePolicy(bool ignoreCase, int expectedFileCount)
    {
        using var workspace = MatrixWorkspace.Create("cdidx_filename_case_matrix");
        workspace.WriteText("dockerfile", "FROM scratch\n");
        workspace.WriteText("makefile.dev", "all:\n\t@true\n");
        var indexer = new FileIndexer(
            workspace.Root,
            ignoreCase,
            ignoreRuleRoot: null,
            maxFileSizeBytes: null,
            directoryIgnoreCaseProbe: _ => ignoreCase);

        var result = indexer.ScanFilesDetailed();

        Assert.Equal(expectedFileCount, result.Files.Count);
        if (ignoreCase)
        {
            Assert.Equal("dockerfile", result.FileLanguages[workspace.FullPath("dockerfile")]);
            Assert.Equal("makefile", result.FileLanguages[workspace.FullPath("makefile.dev")]);
        }
    }

    [Fact]
    public void CaseProbeMatrix_NumericRootUsesPrivateChildWithoutMutatingAncestor()
    {
        using var workspace = MatrixWorkspace.Create("cdidx_case_probe_parent");
        var numericRoot = workspace.FullPath("12345");
        Directory.CreateDirectory(numericRoot);

        Assert.False(CaseSensitivityProbeDirectory.TryCreateLeafNameCaseVariant(numericRoot, out var unchanged));
        Assert.Equal(numericRoot, unchanged);

        _ = CaseSensitivityProbeDirectory.ProbeIgnoreCase(numericRoot, "case-probe-test-");

        Assert.False(Directory.Exists(Path.Combine(numericRoot, CaseSensitivityProbeDirectory.DataDirectoryName)));
    }

    [Fact]
    public void CaseProbeMatrix_ExistingCdidxFileDoesNotBlockPrivateChild_Issue4601()
    {
        using var workspace = MatrixWorkspace.Create("cdidx_case_probe_file");
        var dataPath = workspace.FullPath(CaseSensitivityProbeDirectory.DataDirectoryName);
        File.WriteAllText(dataPath, "reserved");

        _ = CaseSensitivityProbeDirectory.ProbeIgnoreCase(workspace.Root, "case-probe-test-");

        Assert.Equal("reserved", File.ReadAllText(dataPath));
        Assert.Empty(Directory.GetDirectories(
            workspace.Root,
            $"{CaseSensitivityProbeDirectory.IsolatedProbeDirectoryPrefix}*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CaseProbeMatrix_ConcurrentProbesDoNotLeaveSharedArtifacts_Issue4601()
    {
        using var workspace = MatrixWorkspace.Create("cdidx_case_probe_parallel");

        Parallel.For(0, 32, _ =>
            CaseSensitivityProbeDirectory.ProbeIgnoreCase(workspace.Root, "case-probe-test-"));

        Assert.False(Directory.Exists(Path.Combine(workspace.Root, CaseSensitivityProbeDirectory.DataDirectoryName)));
        Assert.Empty(Directory.GetDirectories(
            workspace.Root,
            $"{CaseSensitivityProbeDirectory.IsolatedProbeDirectoryPrefix}*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void CaseProbeMatrix_ExistingChildProbeIgnoresParentSiblingCaseCollision_Issue4601()
    {
        using var workspace = MatrixWorkspace.Create("cdidx_case_probe_sibling_collision");
        if (CaseSensitivityProbeDirectory.ProbeIgnoreCase(workspace.Root, "case-probe-test-"))
            return;

        var target = workspace.FullPath("foo");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(workspace.FullPath("foO"));
        File.WriteAllText(Path.Combine(target, "dockerfile"), "FROM scratch\n");

        Assert.False(CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(target));
    }

    [Fact]
    public void CaseProbeMatrix_SnapshotIsBoundedAndCancelable_Issue5160()
    {
        using var workspace = MatrixWorkspace.Create("cdidx_case_probe_snapshot");
        var target = workspace.FullPath("target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "dockerfile"), "FROM scratch\n");
        File.WriteAllText(Path.Combine(target, "makefile"), "all:\n\t@true\n");
        var entries = Directory.EnumerateFileSystemEntries(target).ToArray();

        using var canceledWriteProbe = new CancellationTokenSource();
        canceledWriteProbe.Cancel();
        var canceledWriteException = Assert.Throws<OperationCanceledException>(() =>
            CaseSensitivityProbeDirectory.ProbeIgnoreCase(
                workspace.Root,
                "case-probe-test-",
                canceledWriteProbe.Token));
        Assert.Equal(canceledWriteProbe.Token, canceledWriteException.CancellationToken);
        Assert.Empty(Directory.GetDirectories(
            workspace.Root,
            $"{CaseSensitivityProbeDirectory.IsolatedProbeDirectoryPrefix}*",
            SearchOption.TopDirectoryOnly));

        var legacyResult = CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(target);
        var snapshotResult = CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(
            target,
            entries,
            maxEntries: entries.Length);

        Assert.Equal(legacyResult, snapshotResult);
        Assert.Null(CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(target, maxEntries: 1));

        var entriesObserved = 0;
        IEnumerable<string> CountedEntries()
        {
            foreach (var entry in entries)
            {
                entriesObserved++;
                yield return entry;
            }
        }

        Assert.Null(CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(
            target,
            CountedEntries(),
            maxEntries: 1));
        Assert.Equal(2, entriesObserved);

        using var preCanceled = new CancellationTokenSource();
        preCanceled.Cancel();
        var preCanceledException = Assert.Throws<OperationCanceledException>(() =>
            CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(
                target,
                entries,
                preCanceled.Token,
                entries.Length));
        Assert.Equal(preCanceled.Token, preCanceledException.CancellationToken);

        using var canceledDuringEnumeration = new CancellationTokenSource();
        IEnumerable<string> CancelingEntries()
        {
            yield return entries[0];
            canceledDuringEnumeration.Cancel();
            yield return entries[1];
        }

        var midEnumerationException = Assert.Throws<OperationCanceledException>(() =>
            CaseSensitivityProbeDirectory.ProbeExistingChildIgnoreCase(
                target,
                CancelingEntries(),
                canceledDuringEnumeration.Token,
                entries.Length));
        Assert.Equal(canceledDuringEnumeration.Token, midEnumerationException.CancellationToken);
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
