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
            Assert.Empty(Directory.GetFiles(projectRoot, ".settings.json.*.tmp"));
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
            Assert.Empty(Directory.GetFiles(projectRoot, ".settings.json.*.tmp"));
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
            Assert.Contains("parent directory could not be flushed", ex.Message, StringComparison.Ordinal);
            Assert.Equal("new", File.ReadAllText(path, Utf8NoBom));
        }
        finally
        {
            AtomicFileWriter.FlushParentDirectoryForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
