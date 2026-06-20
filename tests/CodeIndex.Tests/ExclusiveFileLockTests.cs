using System.Globalization;
using CodeIndex.Cli;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public class ExclusiveFileLockTests
{
    [Fact]
    public void Open_AppliesPrivateFileModeOnUnix_Issue3687()
    {
        var dir = TestProjectHelper.CreateTempProject("cdidx_exclusive_file_lock");
        try
        {
            var lockPath = Path.Combine(dir, "codeindex.db.lock");

            using var stream = ExclusiveFileLock.Open(lockPath);

            Assert.True(stream.CanRead);
            AssertPrivateFileMode(lockPath);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void IndexLock_AcquireWritesPrivateHolderInfoAndReportsContender_Issue3687()
    {
        var dir = TestProjectHelper.CreateTempProject("cdidx_index_lock_contended");
        try
        {
            var dbPath = Path.Combine(dir, "codeindex.db");
            var lockPath = IndexLock.GetLockPath(dbPath);
            using var held = IndexLock.Acquire(lockPath, dir);

            var infoPath = IndexLock.GetInfoPath(lockPath);
            AssertPrivateFileMode(lockPath);
            AssertPrivateFileMode(infoPath);
            var holder = IndexLock.TryReadHolderInfo(lockPath);
            Assert.NotNull(holder);
            Assert.Equal(Environment.ProcessId, holder!.Pid);

            var ex = Assert.Throws<IndexLockConflictException>(() => IndexLock.Acquire(lockPath, dir));
            Assert.NotNull(ex.Holder);
            Assert.Equal(Environment.ProcessId, ex.Holder!.Pid);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void McpIndexRunLock_TryAcquireWritesPrivateHolderInfoAndReportsContender_Issue3687()
    {
        var dir = TestProjectHelper.CreateTempProject("cdidx_mcp_index_lock_contended");
        try
        {
            var dbPath = Path.Combine(dir, "codeindex.db");

            Assert.True(McpIndexRunLock.TryAcquire(dbPath, out var held, out var error));
            Assert.NotNull(held);
            Assert.Null(error);
            using (held)
            {
                var lockPath = McpIndexRunLock.ResolveLockPath(dbPath);
                var infoPath = lockPath + ".info";
                AssertPrivateFileMode(lockPath);
                AssertPrivateFileMode(infoPath);

                Assert.False(McpIndexRunLock.TryAcquire(dbPath, out var contender, out var busy));
                Assert.Null(contender);
                Assert.NotNull(busy);
                Assert.Contains("index already running on this DB", busy);
                Assert.Contains(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), busy);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(dir);
        }
    }

    private static void AssertPrivateFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        var mode = File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits;
        Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
    }
}
