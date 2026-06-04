using System.IO.Compression;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class ExportImportCommandRunnerTests
{
    [Fact]
    public void RunImport_RejectsOversizedManifestBeforeDatabaseEntry()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_manifest_size_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var manifest = new string(' ', ExportImportCommandRunner.MaxImportManifestBytes + 1);
            var archivePath = CreateArchiveWithManifest(workDir, manifest);
            var dbPath = Path.Combine(workDir, "codeindex.db");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath], new JsonSerializerOptions()));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("archive manifest is invalid: archive manifest.json is too large", stderr);
            Assert.DoesNotContain("archive is missing codeindex.db", stderr);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void RunImport_RejectsDeepManifestBeforeDatabaseEntry()
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_manifest_depth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var depth = ExportImportCommandRunner.MaxImportManifestJsonDepth + 4;
            var manifest =
                "{\"format_version\":\"1\",\"cdidx_version\":\"test\",\"user_version\":0,\"database_sha256\":\"" +
                new string('0', 64) +
                "\",\"nested\":" +
                string.Concat(Enumerable.Repeat("{\"x\":", depth)) +
                "0" +
                new string('}', depth) +
                "}";
            var archivePath = CreateArchiveWithManifest(workDir, manifest);
            var dbPath = Path.Combine(workDir, "codeindex.db");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archivePath, "--db", dbPath], new JsonSerializerOptions()));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains($"manifest.json exceeds the JSON depth limit of {ExportImportCommandRunner.MaxImportManifestJsonDepth}", stderr);
            Assert.DoesNotContain("archive is missing codeindex.db", stderr);
            Assert.False(File.Exists(dbPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void RunExportCtags_RejectsDatabaseAndSidecarOutputPaths(string outputSuffix)
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ctags_output_guard");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var outputPath = dbPath + outputSuffix;
            var outputExisted = File.Exists(outputPath);
            var outputInfo = outputExisted ? new FileInfo(outputPath) : null;
            var outputLength = outputInfo?.Length;
            var outputLastWriteUtc = outputInfo?.LastWriteTimeUtc;

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(
                    ["ctags", "--db", dbPath, "--output", outputPath],
                    new JsonSerializerOptions(),
                    "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("ctags output path must not be the source database or a SQLite sidecar", stderr);
            Assert.Equal(outputExisted, File.Exists(outputPath));
            if (outputInfo != null)
            {
                outputInfo.Refresh();
                Assert.Equal(outputLength, outputInfo.Length);
                Assert.Equal(outputLastWriteUtc, outputInfo.LastWriteTimeUtc);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_FailureOmitsRawExceptionMessage()
    {
        var workDir = TestProjectHelper.CreateTempProject("import_error_sanitize");
        try
        {
            var archiveDirectory = Path.Combine(workDir, "archive-directory");
            Directory.CreateDirectory(archiveDirectory);
            var dbPath = Path.Combine(workDir, "codeindex.db");

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport([archiveDirectory, "--db", dbPath], new JsonSerializerOptions()));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("import failed (", stderr);
            Assert.DoesNotContain(archiveDirectory, stderr);
            Assert.DoesNotContain(Path.GetFileName(archiveDirectory), stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RunExportArchive_FailureOmitsRawExceptionMessage()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("export_error_sanitize");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var outputDirectory = Path.Combine(projectRoot, "archive-output");
            Directory.CreateDirectory(outputDirectory);

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport([outputDirectory, "--db", dbPath], new JsonSerializerOptions(), "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("export failed (", stderr);
            Assert.DoesNotContain(outputDirectory, stderr);
            Assert.DoesNotContain(Path.GetFileName(outputDirectory), stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void CreateDatabaseSnapshot_AppliesPrivateFileMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("export_snapshot_mode");
        try
        {
            var sourceDbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var snapshotPath = Path.Combine(projectRoot, "snapshot.db");

            ExportImportCommandRunner.CreateDatabaseSnapshot(sourceDbPath, snapshotPath);

            var mode = File.GetUnixFileMode(snapshotPath) & DataDirectorySecurity.PermissionBits;
            Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunExportCtags_FailureOmitsRawExceptionMessage()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ctags_error_sanitize");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var outputDirectory = Path.Combine(projectRoot, "tags-output");
            Directory.CreateDirectory(outputDirectory);

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunExport(["ctags", "--db", dbPath, "--output", outputDirectory], new JsonSerializerOptions(), "test"));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("ctags export failed (", stderr);
            Assert.DoesNotContain(outputDirectory, stderr);
            Assert.DoesNotContain(Path.GetFileName(outputDirectory), stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

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
    public void ReplaceImportedDatabase_AppliesPrivateFileMode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var workDir = Path.Combine(Path.GetTempPath(), $"cdidx_import_mode_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var dbPath = Path.Combine(workDir, "codeindex.db");
            var tempPath = Path.Combine(workDir, "staged.db");
            File.WriteAllText(dbPath, "existing db");
            File.WriteAllText(tempPath, "imported db");
            File.SetUnixFileMode(tempPath, DataDirectorySecurity.PermissionBits);

            ExportImportCommandRunner.ReplaceImportedDatabase(tempPath, dbPath);

            var mode = File.GetUnixFileMode(dbPath) & DataDirectorySecurity.PermissionBits;
            Assert.Equal(DataDirectorySecurity.PrivateFileMode, mode);
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

    private static string CreateArchiveWithManifest(string workDir, string manifest)
    {
        var archivePath = Path.Combine(workDir, "codeindex.cdidx.zip");
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("manifest.json");
        using var writer = new StreamWriter(entry.Open());
        writer.Write(manifest);
        return archivePath;
    }
}
