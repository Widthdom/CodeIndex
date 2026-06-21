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
    public void IsIgnoreCase_ProbeFailureThrowsStructuredFilesystemError_Issue3439()
    {
        PathCasing.ResetCacheForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_probe_failure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
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
    public void NormalizeBoundaryPath_TrimsTrailingSeparatorsButKeepsRoot_Issue3682()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_normalize_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var withSeparator = tempDir + Path.DirectorySeparatorChar;

            Assert.Equal(Path.GetFullPath(tempDir), PathCasing.NormalizeBoundaryPath(withSeparator));

            var root = Path.GetPathRoot(Path.GetFullPath(tempDir));
            Assert.False(string.IsNullOrEmpty(root));
            Assert.Equal(root, PathCasing.NormalizeBoundaryPath(root!));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IsFullPathEqualOrParent_UsesSeededCaseSensitivity_Issue3682()
    {
        PathCasing.ResetCacheForTests();
        var tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_pathcasing_full_parent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
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
