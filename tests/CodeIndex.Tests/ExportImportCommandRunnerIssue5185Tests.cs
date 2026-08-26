using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class ExportImportCommandRunnerIssue5185Tests
{
    private const uint PosixUmask022 = 0x12;
    private const UnixFileMode SharedDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [Fact]
    public void RunImport_StagingRemainsPrivateFromCreationThroughReplacement_Issue5185()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_private_staging_issue5185");
        uint? previousUmask = null;
        try
        {
            var archivePath = CreateExportArchive(projectRoot);
            var targetRoot = Path.Combine(projectRoot, "target");
            var targetDbPath = TestProjectHelper.CreateProjectDb(targetRoot);
            var targetDirectory = Path.GetDirectoryName(targetDbPath)!;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(targetDirectory, SharedDirectoryMode);
                previousUmask = Umask(PosixUmask022);
            }

            var observationCount = 0;
            ExportImportCommandRunner.ImportStagingFilesHardenedForTesting = stagingPath =>
            {
                observationCount++;
                Assert.Equal(targetDirectory, Path.GetDirectoryName(stagingPath));
                Assert.StartsWith(".codeindex-import-", Path.GetFileName(stagingPath), StringComparison.Ordinal);
                if (observationCount == 1)
                    Assert.Equal(0, new FileInfo(stagingPath).Length);
                AssertPrivateSqliteFileSet(stagingPath);
            };

            var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport(
                    [archivePath, "--db", targetDbPath, "--prune-paths"],
                    new JsonSerializerOptions()));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Imported CodeIndex database", stdout, StringComparison.Ordinal);
            Assert.Equal(string.Empty, stderr);
            Assert.True(observationCount >= 10, $"Expected staging checks across import phases; observed {observationCount}.");
            AssertPrivateSqliteFileSet(targetDbPath);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(targetDirectory),
                path => Path.GetFileName(path).StartsWith(".codeindex-import-", StringComparison.Ordinal));
        }
        finally
        {
            ExportImportCommandRunner.ImportStagingFilesHardenedForTesting = null;
            if (previousUmask.HasValue)
                Umask(previousUmask.Value);
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunImport_CancellationAfterPrivateCreationCleansStagingFiles_Issue5185()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_private_staging_cancel_issue5185");
        uint? previousUmask = null;
        string? stagingPath = null;
        try
        {
            var archivePath = CreateExportArchive(projectRoot);
            var targetDirectory = Path.Combine(projectRoot, "shared-target");
            Directory.CreateDirectory(targetDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(targetDirectory, SharedDirectoryMode);
                previousUmask = Umask(PosixUmask022);
            }

            using var cancellation = new CancellationTokenSource();
            ExportImportCommandRunner.ImportStagingFilesHardenedForTesting = path =>
            {
                if (stagingPath != null)
                    return;

                stagingPath = path;
                Assert.Equal(0, new FileInfo(path).Length);
                AssertPrivateSqliteFileSet(path);
                cancellation.Cancel();
            };

            var targetDbPath = Path.Combine(targetDirectory, "codeindex.db");
            var (exitCode, _, _) = ConsoleCapture.Capture(() =>
                ExportImportCommandRunner.RunImport(
                    [archivePath, "--db", targetDbPath],
                    new JsonSerializerOptions(),
                    cancellation.Token));

            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.NotNull(stagingPath);
            Assert.False(File.Exists(stagingPath));
            Assert.False(File.Exists(stagingPath + "-wal"));
            Assert.False(File.Exists(stagingPath + "-shm"));
            Assert.False(File.Exists(targetDbPath));
        }
        finally
        {
            ExportImportCommandRunner.ImportStagingFilesHardenedForTesting = null;
            if (previousUmask.HasValue)
                Umask(previousUmask.Value);
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ExtractDatabaseEntryToFile_CreateNewPreservesCollision_Issue5185()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("import_private_staging_collision_issue5185");
        try
        {
            var archivePath = Path.Combine(projectRoot, "payload.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("codeindex.db");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("replacement bytes");
            }

            var stagingPath = Path.Combine(projectRoot, ".codeindex-import-collision.db");
            File.WriteAllText(stagingPath, "existing bytes");
            using var readArchive = ZipFile.OpenRead(archivePath);
            var databaseEntry = readArchive.GetEntry("codeindex.db")!;

            Assert.Throws<IOException>(() =>
                ExportImportCommandRunner.ExtractDatabaseEntryToFile(
                    databaseEntry,
                    stagingPath,
                    CancellationToken.None));
            Assert.Equal("existing bytes", File.ReadAllText(stagingPath));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PrivateStagingDatabase_CreatesAndRehardensPrivateWalAndShm_Issue5185()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject("import_private_staging_sidecars_issue5185");
        uint? previousUmask = null;
        try
        {
            var archivePath = CreateExportArchive(projectRoot);
            var stagingDirectory = Path.Combine(projectRoot, "shared-staging");
            Directory.CreateDirectory(stagingDirectory);
            File.SetUnixFileMode(stagingDirectory, SharedDirectoryMode);
            previousUmask = Umask(PosixUmask022);

            var stagingPath = Path.Combine(stagingDirectory, ".codeindex-import-sidecars.db");
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                ExportImportCommandRunner.ExtractDatabaseEntryToFile(
                    archive.GetEntry("codeindex.db")!,
                    stagingPath,
                    CancellationToken.None);
            }

            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = stagingPath, Pooling = false }.ConnectionString);
            connection.Open();
            using (var journalMode = connection.CreateCommand())
            {
                journalMode.CommandText = "PRAGMA journal_mode = WAL";
                Assert.Equal("wal", Convert.ToString(journalMode.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }

            using var transaction = connection.BeginTransaction();
            using (var write = connection.CreateCommand())
            {
                write.Transaction = transaction;
                write.CommandText = "INSERT INTO codeindex_meta(key, value) VALUES ('issue5185', 'private') ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                write.ExecuteNonQuery();
            }

            Assert.True(File.Exists(stagingPath + "-wal"));
            Assert.True(File.Exists(stagingPath + "-shm"));
            AssertPrivateSqliteFileSet(stagingPath);

            File.SetUnixFileMode(stagingPath + "-wal", DataDirectorySecurity.PermissionBits);
            File.SetUnixFileMode(stagingPath + "-shm", DataDirectorySecurity.PermissionBits);
            ExportImportCommandRunner.EnsureImportStagingFilesPrivate(stagingPath);
            AssertPrivateSqliteFileSet(stagingPath);
            transaction.Rollback();
        }
        finally
        {
            if (previousUmask.HasValue)
                Umask(previousUmask.Value);
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string CreateExportArchive(string projectRoot)
    {
        var sourceRoot = Path.Combine(projectRoot, $"source-{Guid.NewGuid():N}");
        var sourceDbPath = TestProjectHelper.CreateProjectDb(sourceRoot);
        TestProjectHelper.InsertIndexedFile(
            sourceDbPath,
            "src/Secret.cs",
            "csharp",
            "namespace Secret; public sealed class TokenStore { }\n");
        var archivePath = Path.Combine(projectRoot, $"archive-{Guid.NewGuid():N}.cdidx.zip");
        var result = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunExport(
                [archivePath, "--db", sourceDbPath],
                new JsonSerializerOptions(),
                "test"));
        Assert.Equal(CommandExitCodes.Success, result.ExitCode);
        Assert.Equal(string.Empty, result.Stderr);
        return archivePath;
    }

    private static void AssertPrivateSqliteFileSet(string databasePath)
    {
        if (OperatingSystem.IsWindows())
            return;

        AssertPrivateMode(databasePath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
                AssertPrivateMode(path);
        }
    }

    private static void AssertPrivateMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal(
            DataDirectorySecurity.PrivateFileMode,
            File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits);
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);
}
