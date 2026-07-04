using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class FileSystemBoundaryTests
{
    [Fact]
    public void TryValidateDirectoryCleanupTarget_RejectsSiblingWithSharedPrefix_Issue3970()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"cdidx-boundary-{Guid.NewGuid():N}");
        var sibling = parent + "-sibling";
        var target = Path.Combine(sibling, "cleanup-target");

        var valid = FileSystemBoundary.TryValidateDirectoryCleanupTarget(
            target,
            parent,
            CreateOptions(),
            out _,
            out var failureReason);

        Assert.False(valid);
        Assert.Equal("outside", failureReason);
    }

    [Fact]
    public void TryValidateDirectoryCleanupTarget_UsesPathCasingProbe_Issue3970()
    {
        lock (PathCasingTestLock.Gate)
        {
            var previousProbe = PathCasing.IgnoreCaseProbeForTesting;
            try
            {
                PathCasing.ResetCacheForTests();
                PathCasing.IgnoreCaseProbeForTesting = _ => true;
                var root = Path.Combine(Path.GetTempPath(), $"CDIDX-BOUNDARY-{Guid.NewGuid():N}");
                var target = Path.Combine(root.ToLowerInvariant(), "cleanup-target");

                var valid = FileSystemBoundary.TryValidateDirectoryCleanupTarget(
                    target,
                    root,
                    CreateOptions(),
                    out _,
                    out var failureReason);

                Assert.True(valid, failureReason);
            }
            finally
            {
                PathCasing.IgnoreCaseProbeForTesting = previousProbe;
                PathCasing.ResetCacheForTests();
            }
        }
    }

    [Fact]
    public void TryValidateDirectoryCleanupTarget_RejectsSymlinkDirectory_Issue3970()
    {
        if (OperatingSystem.IsWindows())
            return;

        var safeRoot = TestProjectHelper.CreateTempProject("cdidx_boundary_safe");
        var externalRoot = TestProjectHelper.CreateTempProject("cdidx_boundary_external");
        var link = Path.Combine(safeRoot, "cleanup-link");
        try
        {
            Directory.CreateSymbolicLink(link, externalRoot);

            var valid = FileSystemBoundary.TryValidateDirectoryCleanupTarget(
                link,
                safeRoot,
                CreateOptions(),
                out _,
                out var failureReason);

            Assert.False(valid);
            Assert.Equal("unsafe", failureReason);
        }
        finally
        {
            DeletePathOrSymlink(link);
            TestProjectHelper.DeleteDirectory(safeRoot);
            TestProjectHelper.DeleteDirectory(externalRoot);
        }
    }

    [Fact]
    public void TryValidateDirectoryCleanupTarget_RejectsBrokenSymlinkDirectory_Issue4260()
    {
        if (OperatingSystem.IsWindows())
            return;

        var safeRoot = TestProjectHelper.CreateTempProject("cdidx_boundary_safe");
        var missingTarget = Path.Combine(Path.GetTempPath(), $"cdidx_boundary_missing_{Guid.NewGuid():N}");
        var link = Path.Combine(safeRoot, "cleanup-link");
        try
        {
            Directory.CreateSymbolicLink(link, missingTarget);

            var valid = FileSystemBoundary.TryValidateDirectoryCleanupTarget(
                link,
                safeRoot,
                CreateOptions(),
                out _,
                out var failureReason);

            Assert.False(valid);
            Assert.Equal("unsafe", failureReason);
        }
        finally
        {
            DeletePathOrSymlink(link);
            TestProjectHelper.DeleteDirectory(safeRoot);
            TestProjectHelper.DeleteDirectory(missingTarget);
        }
    }

    private static DirectoryCleanupBoundaryOptions CreateOptions()
        => new(
            "cleanup-",
            "outside",
            "prefix",
            "unsafe");

    private static void DeletePathOrSymlink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                File.Delete(path);
                return;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }
}
