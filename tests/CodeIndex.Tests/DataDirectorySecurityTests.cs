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
}
