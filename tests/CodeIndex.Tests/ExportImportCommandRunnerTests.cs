using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class ExportImportCommandRunnerTests
{
    [Fact]
    public void WriteExportArchiveFile_FailurePreservesExistingArchive()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var outputPath = Path.Combine(workDir, "codeindex.cdidx.zip");
            File.WriteAllText(outputPath, "existing archive");
            var missingSnapshotPath = Path.Combine(workDir, "missing.db");
            var manifest = new ExportImportCommandRunner.ExportManifest(
                "1",
                "test",
                0,
                null,
                null,
                new string('0', 64));

            Assert.Throws<FileNotFoundException>(() =>
                ExportImportCommandRunner.WriteExportArchiveFile(
                    outputPath,
                    missingSnapshotPath,
                    manifest,
                    new JsonSerializerOptions()));

            Assert.Equal("existing archive", File.ReadAllText(outputPath));
            Assert.Single(Directory.GetFiles(workDir));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void WriteCtagsFile_FailurePreservesExistingTagfile()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_ctags_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var outputPath = Path.Combine(workDir, "tags");
            File.WriteAllText(outputPath, "existing tags");

            Assert.Throws<IOException>(() =>
                ExportImportCommandRunner.WriteCtagsFile(
                    outputPath,
                    writer =>
                    {
                        writer.WriteLine("partial");
                        throw new IOException("simulated ctags failure");
                    }));

            Assert.Equal("existing tags", File.ReadAllText(outputPath));
            Assert.Single(Directory.GetFiles(workDir));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_MoveFailurePreservesExistingSidecars()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(dbPath + "-wal", "existing wal");
            File.WriteAllText(dbPath + "-shm", "existing shm");
            var missingTempPath = Path.Combine(workDir, "missing.db");

            Assert.ThrowsAny<IOException>(() =>
                ExportImportCommandRunner.ReplaceImportedDatabase(missingTempPath, dbPath));

            Assert.Equal("existing db", File.ReadAllText(dbPath));
            Assert.Equal("existing wal", File.ReadAllText(dbPath + "-wal"));
            Assert.Equal("existing shm", File.ReadAllText(dbPath + "-shm"));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void ReplaceImportedDatabase_SuccessDeletesDestinationSidecarsAfterMove()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(dbPath + "-wal", "existing wal");
            File.WriteAllText(dbPath + "-shm", "existing shm");
            File.WriteAllText(tempPath, "imported db");

            ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath);

            Assert.Equal("imported db", File.ReadAllText(dbPath));
            Assert.False(File.Exists(dbPath + "-wal"));
            Assert.False(File.Exists(dbPath + "-shm"));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void TryValidateDatabaseEntrySize_RejectsOversizedUncompressedLength()
    {
        var ok = ExportImportCommandRunner.TryValidateDatabaseEntrySize(
            uncompressedLength: ExportImportCommandRunner.MaxImportDatabaseBytes + 1,
            compressedLength: 1,
            message: out var message);

        Assert.False(ok);
        Assert.Contains("uncompressed exceeds the import limit", message);
    }

    [Fact]
    public void TryValidateDatabaseEntrySize_RejectsOversizedCompressedLength()
    {
        var ok = ExportImportCommandRunner.TryValidateDatabaseEntrySize(
            uncompressedLength: 1,
            compressedLength: ExportImportCommandRunner.MaxImportDatabaseBytes + 1,
            message: out var message);

        Assert.False(ok);
        Assert.Contains("compressed exceeds the import limit", message);
    }

    [Fact]
    public void CopyToWithLimit_ThrowsBeforeWritingPastLimit()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();

        var ex = Assert.Throws<InvalidDataException>(() => ExportImportCommandRunner.CopyToWithLimit(source, target, maxBytes: 3));

        Assert.Contains("codeindex.db exceeds the import limit", ex.Message);
        Assert.Equal(0, target.Length);
    }

    [Fact]
    public void CopyToWithLimit_AllowsExactLimit()
    {
        using var source = new MemoryStream([1, 2, 3, 4]);
        using var target = new MemoryStream();

        var copied = ExportImportCommandRunner.CopyToWithLimit(source, target, maxBytes: 4);

        Assert.Equal(4, copied);
        Assert.Equal([1, 2, 3, 4], target.ToArray());
    }
}
