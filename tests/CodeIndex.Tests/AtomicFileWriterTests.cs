using System.Runtime.InteropServices;
using System.Text;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class AtomicFileWriterTests
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void WriteText_ReplacesFileAndFlushesParentDirectoryWhenSupported()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_replace");
        try
        {
            var path = Path.Combine(projectRoot, "settings.json");
            File.WriteAllText(path, "old", Utf8NoBom);

            AtomicFileWriter.WriteText(path, "new", Utf8NoBom);

            Assert.Equal("new", File.ReadAllText(path, Utf8NoBom));
            Assert.Empty(Directory.GetFiles(projectRoot, "*.tmp"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void WriteText_ModeFailureBeforeReplace_LeavesExistingFile()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_mode_failure");
        try
        {
            var path = Path.Combine(projectRoot, "settings.json");
            File.WriteAllText(path, "old", Utf8NoBom);
            string? modePath = null;

            Assert.Throws<UnauthorizedAccessException>(() =>
                AtomicFileWriter.WriteText(
                    path,
                    "new",
                    Utf8NoBom,
                    tempPath =>
                    {
                        modePath = tempPath;
                        throw new UnauthorizedAccessException("mode denied");
                    }));

            Assert.NotNull(modePath);
            Assert.NotEqual(path, modePath);
            Assert.Equal("old", File.ReadAllText(path, Utf8NoBom));
            Assert.Empty(Directory.GetFiles(projectRoot, "*.tmp"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void WriteText_ParentDirectoryFlushFailure_ReportsPostReplaceDurabilityFailure()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_flush_failure");
        try
        {
            var path = Path.Combine(projectRoot, "settings.json");
            File.WriteAllText(path, "old", Utf8NoBom);
            AtomicFileWriter.FlushParentDirectoryForTesting = _ => throw new IOException("flush failed");

            var ex = Assert.Throws<IOException>(() =>
                AtomicFileWriter.WriteText(path, "new", Utf8NoBom));

            Assert.Contains("Atomic replace completed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("target file was already replaced", ex.Message, StringComparison.Ordinal);
            Assert.Contains("parent directory could not be flushed", ex.Message, StringComparison.Ordinal);
            Assert.Equal("new", File.ReadAllText(path, Utf8NoBom));
        }
        finally
        {
            AtomicFileWriter.FlushParentDirectoryForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void WriteJson_SensitiveProfile_WritesPrivateFileAndFlushesParentDirectory_Issue3688()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_sensitive");
        try
        {
            var path = Path.Combine(projectRoot, "scan-checkpoint.json");
            string? flushedDirectory = null;
            AtomicFileWriter.FlushParentDirectoryForTesting = directory => flushedDirectory = directory;

            AtomicFileWriter.WriteJson(
                path,
                new { current_head = "HEAD", directories = new[] { "src" } },
                AtomicFileWriter.WriteProfile.Sensitive);

            Assert.Equal(projectRoot, flushedDirectory);
            Assert.Contains("\"current_head\"", File.ReadAllText(path, Utf8NoBom), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(projectRoot, "*.tmp"));

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var mode = File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits;
                Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
            }
        }
        finally
        {
            AtomicFileWriter.FlushParentDirectoryForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void BuildTempPathForTesting_BoundsTempFileNameForLongTargets_Issue3776()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_long_name");
        try
        {
            var targetName = new string('a', 240) + ".json";
            var path = Path.Combine(projectRoot, targetName);

            var tempPath = AtomicFileWriter.BuildTempPathForTesting(path);
            var tempName = Path.GetFileName(tempPath);

            Assert.Equal(projectRoot, Path.GetDirectoryName(tempPath));
            Assert.StartsWith(".cdidx-", tempName, StringComparison.Ordinal);
            Assert.EndsWith(".tmp", tempName, StringComparison.Ordinal);
            Assert.True(
                tempName.Length <= AtomicFileWriter.MaxTempFileNameChars,
                $"temp file name was {tempName.Length} chars: {tempName}");
            Assert.DoesNotContain(targetName, tempName, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
