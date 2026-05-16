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
    {
        PathCasing.ResetCacheForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var expected = ProbeDirectoryIgnoreCaseLikeProduction(tempDir);
            Assert.Equal(expected, PathCasing.IsIgnoreCase(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IsIgnoreCase_CachesResultPerAnchor()
    {
        PathCasing.ResetCacheForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var initial = PathCasing.IsIgnoreCase(tempDir);

            // Removing the directory after the cache is populated must not flip the
            // answer — probes happen at most once per anchor.
            Directory.Delete(tempDir, recursive: true);

            Assert.Equal(initial, PathCasing.IsIgnoreCase(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SeedFromWorkspace_OverridesSubsequentProbes()
    {
        PathCasing.ResetCacheForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_seed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: true);
            Assert.True(PathCasing.IsIgnoreCase(tempDir));

            PathCasing.ResetCacheForTests();
            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
            Assert.False(PathCasing.IsIgnoreCase(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PathsEqual_UsesSeededIgnoreCase()
    {
        PathCasing.ResetCacheForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_equal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mixed = Path.Combine(tempDir, "Foo");
            var lowered = Path.Combine(tempDir, "foo");

            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: true);
            Assert.True(PathCasing.PathsEqual(mixed, lowered));

            PathCasing.ResetCacheForTests();
            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
            Assert.False(PathCasing.PathsEqual(mixed, lowered));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IsPathEqualOrParent_RespectsCaseSensitiveSeed()
    {
        PathCasing.ResetCacheForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_parent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var parent = Path.Combine(tempDir, "Project");
            var child = Path.Combine(tempDir, "project", "src", "App.cs");

            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
            Assert.False(PathCasing.IsPathEqualOrParent(parent, child));

            PathCasing.ResetCacheForTests();
            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: true);
            Assert.True(PathCasing.IsPathEqualOrParent(parent, child));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IsPathEqualOrParent_PreventsPrefixCollision()
    {
        PathCasing.ResetCacheForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_prefix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            PathCasing.SeedFromWorkspace(tempDir, ignoreCase: false);
            var parent = Path.Combine(tempDir, "Project");
            var sibling = Path.Combine(tempDir, "ProjectExtras", "App.cs");

            Assert.False(PathCasing.IsPathEqualOrParent(parent, sibling));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PathsEqual_NullEitherSide_IsFalse()
    {
        Assert.False(PathCasing.PathsEqual(null, "/tmp"));
        Assert.False(PathCasing.PathsEqual("/tmp", null));
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
}
