using CodeIndex;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for the centralized filesystem case-sensitivity probe used by all path equality
/// helpers (Issue #1546). The probe must reflect the actual filesystem at <c>path</c>
/// rather than the OS family, so case-sensitive APFS / WSL NTFS / ReFS volumes are
/// classified correctly even when the host happens to be macOS or Windows.
/// #1546: ファイルシステム単位での case-sensitivity プローブの挙動を保証するテスト。
/// </summary>
public class PathCasingTests
{
    [Fact]
    public void IsIgnoreCase_AgreesWithLiveFilesystemProbe()
        => RunWithPathCasingLock(() =>
    {
        PathCasing.ResetCacheForTests();
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing");
        var tempDir = project.Root;

        var expected = ProbeDirectoryIgnoreCaseLikeProduction(tempDir);
        Assert.Equal(expected, PathCasing.IsIgnoreCase(tempDir));
    });

    [Fact]
    public void IsIgnoreCase_CachesResultPerAnchor()
        => RunWithPathCasingLock(() =>
    {
        PathCasing.ResetCacheForTests();
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing_cache");
        var tempDir = project.Root;

        var initial = PathCasing.IsIgnoreCase(tempDir);

        // Removing the directory after the cache is populated must not flip the
        // answer — probes happen at most once per anchor.
        TestProjectHelper.DeleteDirectory(tempDir);

        Assert.Equal(initial, PathCasing.IsIgnoreCase(tempDir));
    });

    [Fact]
    public void SeedFromWorkspace_OverridesSubsequentProbes()
        => RunWithPathCasingLock(() =>
    {
        PathCasing.ResetCacheForTests();
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing_seed");
        var tempDir = project.Root;

        PathCasing.SeedFromWorkspace(tempDir, ignoreCase: true);
        Assert.True(PathCasing.IsIgnoreCase(tempDir));

        PathCasing.ResetCacheForTests();
        PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
        Assert.False(PathCasing.IsIgnoreCase(tempDir));
    });

    [Fact]
    public void IsIgnoreCase_ProbeFailureThrowsStructuredFilesystemError_Issue3439()
        => RunWithPathCasingLock(() =>
    {
        PathCasing.ResetCacheForTests();
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing_probe_failure");
        var tempDir = project.Root;
        var previousProbe = PathCasing.IgnoreCaseProbeForTesting;
        PathCasing.IgnoreCaseProbeForTesting = _ => throw new IOException("probe blocked");
        try
        {
            var ex = Assert.Throws<CodeIndexException>(() => PathCasing.IsIgnoreCase(tempDir));

            Assert.Equal(CommandErrorCodes.FileSystemCaseProbeFailed, ex.Code);
            Assert.Equal(CodeIndexExceptionCategory.Filesystem, ex.Category);
            Assert.Equal(Path.GetFullPath(tempDir), ex.Path);
            Assert.IsType<IOException>(ex.InnerException);
        }
        finally
        {
            PathCasing.IgnoreCaseProbeForTesting = previousProbe;
            PathCasing.ResetCacheForTests();
        }
    });

    [Fact]
    public void PathsEqual_UsesSeededIgnoreCase()
        => RunWithPathCasingLock(() =>
    {
        PathCasing.ResetCacheForTests();
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing_equal");
        var tempDir = project.Root;

        var mixed = Path.Combine(tempDir, "Foo");
        var lowered = Path.Combine(tempDir, "foo");

        PathCasing.SeedFromWorkspace(tempDir, ignoreCase: true);
        Assert.True(PathCasing.PathsEqual(mixed, lowered));

        PathCasing.ResetCacheForTests();
        PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
        Assert.False(PathCasing.PathsEqual(mixed, lowered));
    });

    [Fact]
    public void IsPathEqualOrParent_RespectsCaseSensitiveSeed()
        => RunWithPathCasingLock(() =>
    {
        PathCasing.ResetCacheForTests();
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing_parent");
        var tempDir = project.Root;

        var parent = Path.Combine(tempDir, "Project");
        var child = Path.Combine(tempDir, "project", "src", "App.cs");

        PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
        Assert.False(PathCasing.IsPathEqualOrParent(parent, child));

        PathCasing.ResetCacheForTests();
        PathCasing.SeedFromWorkspace(tempDir, ignoreCase: true);
        Assert.True(PathCasing.IsPathEqualOrParent(parent, child));
    });

    [Fact]
    public void IsPathEqualOrParent_PreventsPrefixCollision()
        => RunWithPathCasingLock(() =>
    {
        PathCasing.ResetCacheForTests();
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing_prefix");
        var tempDir = project.Root;

        PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
        var parent = Path.Combine(tempDir, "Project");
        var sibling = Path.Combine(tempDir, "ProjectExtras", "App.cs");

        Assert.False(PathCasing.IsPathEqualOrParent(parent, sibling));
    });

    [Fact]
    public void NormalizeBoundaryPath_TrimsTrailingSeparatorsButKeepsRoot_Issue3682()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing_normalize");
        var tempDir = project.Root;

        var withSeparator = tempDir + Path.DirectorySeparatorChar;

        Assert.Equal(Path.GetFullPath(tempDir), PathCasing.NormalizeBoundaryPath(withSeparator));

        var root = Path.GetPathRoot(Path.GetFullPath(tempDir));
        Assert.False(string.IsNullOrEmpty(root));
        Assert.Equal(root, PathCasing.NormalizeBoundaryPath(root!));
    }

    [Fact]
    public void IsFullPathEqualOrParent_UsesSeededCaseSensitivity_Issue3682()
        => RunWithPathCasingLock(() =>
    {
        PathCasing.ResetCacheForTests();
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_pathcasing_full_parent");
        var tempDir = project.Root;
        try
        {
            var parent = Path.Combine(tempDir, "Project") + Path.DirectorySeparatorChar;
            var child = Path.Combine(tempDir, "project", "src", "App.cs");

            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
            Assert.False(PathCasing.IsFullPathEqualOrParent(parent, child));

            PathCasing.ResetCacheForTests();
            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: true);
            Assert.True(PathCasing.IsFullPathEqualOrParent(parent, child));
        }
        finally
        {
            PathCasing.ResetCacheForTests();
        }
    });

    [Fact]
    public void PathsEqual_NullEitherSide_IsFalse()
    {
        Assert.False(PathCasing.PathsEqual(null, "/tmp"));
        Assert.False(PathCasing.PathsEqual("/tmp", null));
    }

    private static void RunWithPathCasingLock(Action action)
    {
        lock (PathCasingTestLock.Gate)
            action();
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
