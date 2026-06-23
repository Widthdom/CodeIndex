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
    public void MoveReplacing_ParentDirectoryFlushFailure_ReportsPostReplaceDurabilityFailure_Issue4001()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_move_replace_flush");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "settings.new");
            var destinationPath = Path.Combine(projectRoot, "settings.json");
            File.WriteAllText(sourcePath, "new", Utf8NoBom);
            File.WriteAllText(destinationPath, "old", Utf8NoBom);
            AtomicFileWriter.FlushParentDirectoryForTesting = _ => throw new IOException("flush failed");

            var ex = Assert.Throws<IOException>(() =>
                AtomicFileWriter.MoveReplacing(sourcePath, destinationPath));

            Assert.Contains("Atomic replace completed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("target file was already replaced", ex.Message, StringComparison.Ordinal);
            Assert.Contains("parent directory could not be flushed", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(sourcePath));
            Assert.Equal("new", File.ReadAllText(destinationPath, Utf8NoBom));
        }
        finally
        {
            AtomicFileWriter.FlushParentDirectoryForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void MoveFile_AppliesDestinationModeAfterMove_Issue4001()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_move_mode");
        try
        {
            var sourcePath = Path.Combine(projectRoot, "source.db");
            var destinationPath = Path.Combine(projectRoot, "destination.db");
            string? modePath = null;
            File.WriteAllText(sourcePath, "db", Utf8NoBom);

            AtomicFileWriter.MoveFile(
                sourcePath,
                destinationPath,
                overwrite: false,
                applyDestinationMode: path => modePath = path);

            Assert.False(File.Exists(sourcePath));
            Assert.Equal("db", File.ReadAllText(destinationPath, Utf8NoBom));
            Assert.Equal(destinationPath, modePath);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PublishDirectory_ParentDirectoryFlushFailure_ReportsPublishedDirectory_Issue4001()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_directory_publish_flush");
        try
        {
            var tempPath = Path.Combine(projectRoot, ".tmp-checkpoint");
            var destinationPath = Path.Combine(projectRoot, "checkpoint");
            Directory.CreateDirectory(tempPath);
            File.WriteAllText(Path.Combine(tempPath, "manifest.txt"), "checkpoint", Utf8NoBom);
            AtomicFileWriter.FlushParentDirectoryForTesting = _ => throw new IOException("flush failed");

            var ex = Assert.Throws<IOException>(() =>
                AtomicFileWriter.PublishDirectory(tempPath, destinationPath));

            Assert.Contains("Directory publish completed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("destination directory was already published", ex.Message, StringComparison.Ordinal);
            Assert.Contains("parent directory could not be flushed", ex.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(tempPath));
            Assert.True(File.Exists(Path.Combine(destinationPath, "manifest.txt")));
        }
        finally
        {
            AtomicFileWriter.FlushParentDirectoryForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void TryDeleteFile_CleanupFailureReportsAndSuppresses_Issue4001()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("atomic_cleanup_failure");
        try
        {
            var path = Path.Combine(projectRoot, "cleanup.tmp");
            var failures = new List<Exception>();
            File.WriteAllText(path, "temp", Utf8NoBom);

            var deleted = AtomicFileWriter.TryDeleteFile(
                path,
                failures.Add,
                _ => throw new IOException("delete denied"));

            Assert.False(deleted);
            var failure = Assert.Single(failures);
            Assert.Contains("delete denied", failure.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(path));
        }
        finally
        {
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
