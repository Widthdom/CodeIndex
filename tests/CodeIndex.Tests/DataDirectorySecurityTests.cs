using System.Runtime.InteropServices;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class DataDirectorySecurityTests
{
    [Fact]
    public void CreatePrivateDirectory_OnPosix_Forces0700Mode()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_data_dir_security_{Guid.NewGuid():N}");
        var cdidxDir = Path.Combine(root, ".cdidx");
        try
        {
            DataDirectorySecurity.CreatePrivateDirectory(cdidxDir);

            Assert.Equal("0700", DataDirectorySecurity.GetUnixModeString(cdidxDir));
            Assert.Equal(
                DataDirectorySecurity.PrivateDirectoryMode,
                File.GetUnixFileMode(cdidxDir) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GetUnixModeString_OnMissingDirectory_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"cdidx_missing_data_dir_{Guid.NewGuid():N}");

        Assert.Null(DataDirectorySecurity.GetUnixModeString(missing));
    }

    [Fact]
    public void CreateSensitiveDirectory_OnPosix_Forces0700Mode()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_sensitive_dir_security_{Guid.NewGuid():N}");
        var sensitiveDir = Path.Combine(root, "state");
        try
        {
            DataDirectorySecurity.CreateSensitiveDirectory(sensitiveDir);

            Assert.Equal(
                DataDirectorySecurity.PrivateDirectoryMode,
                File.GetUnixFileMode(sensitiveDir) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateSensitiveParentDirectoryForFile_OnPosix_Forces0700Mode_Issue3775()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_sensitive_parent_security_{Guid.NewGuid():N}");
        var path = Path.Combine(root, "audit", "events.jsonl");
        try
        {
            var directory = DataDirectorySecurity.CreateSensitiveParentDirectoryForFile(path);

            Assert.NotNull(directory);
            Assert.Equal(Path.GetDirectoryName(path), directory.FullName);
            Assert.Equal(
                DataDirectorySecurity.PrivateDirectoryMode,
                File.GetUnixFileMode(directory.FullName) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateSensitiveParentDirectoryForFile_OnExistingTempRoot_DoesNotHardenSharedRoot_Issue3775()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_direct_parent_{Guid.NewGuid():N}.jsonl");

        var directory = DataDirectorySecurity.CreateSensitiveParentDirectoryForFile(path);

        Assert.NotNull(directory);
        Assert.Equal(
            Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            directory.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public void CreateSensitiveTempDirectory_OnPosix_Forces0700Mode()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        DirectoryInfo? tempDir = null;
        try
        {
            tempDir = DataDirectorySecurity.CreateSensitiveTempDirectory("cdidx-sensitive-test-");

            Assert.StartsWith("cdidx-sensitive-test-", tempDir.Name, StringComparison.Ordinal);
            Assert.Equal(
                DataDirectorySecurity.PrivateDirectoryMode,
                File.GetUnixFileMode(tempDir.FullName) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            if (tempDir != null && Directory.Exists(tempDir.FullName))
                Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void ResolveSensitiveTempFallbackDirectory_UsesUserScopedCdidxRoot_Issue3675()
    {
        var directory = DataDirectorySecurity.ResolveSensitiveTempFallbackDirectory("cache");

        Assert.StartsWith(
            Path.Combine(Path.GetTempPath(), "cdidx-u"),
            directory,
            StringComparison.Ordinal);
        Assert.EndsWith(
            Path.Combine("", "cache"),
            directory,
            StringComparison.Ordinal);
        Assert.NotEqual(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cdidx", "cache")),
            directory);
    }

    [Fact]
    public void CreateSensitiveDirectory_OnExistingTempFallbackScopeRoot_HardensRoot_Issue3675()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var directory = DataDirectorySecurity.ResolveSensitiveTempFallbackDirectory($"scope-{Guid.NewGuid():N}");
        var scopeRoot = Directory.GetParent(directory)!.FullName;
        if (Directory.Exists(scopeRoot) || File.Exists(scopeRoot))
            return;

        try
        {
            Directory.CreateDirectory(scopeRoot);
            File.SetUnixFileMode(scopeRoot, DataDirectorySecurity.PermissionBits);

            DataDirectorySecurity.CreateSensitiveDirectory(directory);

            Assert.Equal(
                DataDirectorySecurity.PrivateDirectoryMode,
                File.GetUnixFileMode(scopeRoot) & DataDirectorySecurity.PermissionBits);
            Assert.Equal(
                DataDirectorySecurity.PrivateDirectoryMode,
                File.GetUnixFileMode(directory) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            DeletePathOrSymlink(scopeRoot);
        }
    }

    [Fact]
    public void CreateSensitiveDirectory_OnSymlinkedTempFallbackScopeRoot_RejectsRoot_Issue3675()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var directory = DataDirectorySecurity.ResolveSensitiveTempFallbackDirectory($"symlink-{Guid.NewGuid():N}");
        var scopeRoot = Directory.GetParent(directory)!.FullName;
        if (Directory.Exists(scopeRoot) || File.Exists(scopeRoot))
            return;

        var attackRoot = Path.Combine(Path.GetTempPath(), $"cdidx_sensitive_fallback_attack_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(attackRoot);
            File.CreateSymbolicLink(scopeRoot, attackRoot);

            var ex = Assert.Throws<IOException>(() => DataDirectorySecurity.CreateSensitiveDirectory(directory));

            Assert.Contains("symbolic link or reparse point", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            DeletePathOrSymlink(scopeRoot);
            DeletePathOrSymlink(attackRoot);
        }
    }

    [Fact]
    public void WritePrivateText_OnPosix_Forces0600Mode()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_sensitive_file_security_{Guid.NewGuid():N}");
        var path = Path.Combine(root, "metadata.info");
        try
        {
            Directory.CreateDirectory(root);

            DataDirectorySecurity.WritePrivateText(path, "secret");

            Assert.Equal("secret", File.ReadAllText(path));
            Assert.Equal(
                DataDirectorySecurity.PrivateFileMode,
                File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OpenPrivateFileStream_OnPosix_CreatesPrivateFilesUpFront_Issue3984()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_private_create_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var createNewPath = Path.Combine(root, "watch-spool.jsonl");
            using (var stream = DataDirectorySecurity.OpenPrivateFileStream(
                createNewPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read))
            {
                stream.WriteByte(1);
                AssertPrivateFileMode(createNewPath);
            }

            var openOrCreatePath = Path.Combine(root, "codeindex.db.lock");
            using (var stream = DataDirectorySecurity.OpenPrivateFileStream(
                openOrCreatePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                stream.WriteByte(1);
                AssertPrivateFileMode(openOrCreatePath);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WritePrivateText_MoveFailure_DoesNotLeaveTempFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_sensitive_file_atomic_{Guid.NewGuid():N}");
        var path = Path.Combine(root, "metadata.info");
        try
        {
            Directory.CreateDirectory(path);

            var ex = Record.Exception(() => DataDirectorySecurity.WritePrivateText(path, "secret"));

            Assert.NotNull(ex);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root),
                file => Path.GetFileName(file).EndsWith(".tmp", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadTextWithinLimit_WhenFileExceedsLimit_ReturnsNull()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_bounded_read_{Guid.NewGuid():N}");
        var path = Path.Combine(root, "metadata.info");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, new string('x', 17));

            Assert.Null(DataDirectorySecurity.ReadTextWithinLimit(path, maxBytes: 16));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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

    private static void AssertPrivateFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        var mode = File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits;
        Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
    }
}
