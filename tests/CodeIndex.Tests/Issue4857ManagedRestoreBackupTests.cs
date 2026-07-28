using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Regression coverage for atomic import rollback and managed restore backups.
/// import の原子的ロールバックと管理対象復元バックアップの回帰テスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public sealed class Issue4857ManagedRestoreBackupTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void ImportCreatesManagedBackupThatDryRunValidatesAndRestoreSelectsById()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_issue4857_import_restore");
        try
        {
            var sourceDb = CreateDatabase(root, "source", "src/Imported.cs");
            var destinationDb = CreateDatabase(root, "destination", "src/Original.cs");
            var archivePath = ExportArchive(root, sourceDb);

            var (importExit, importJson) = RunImportJson(
                [archivePath, "--db", destinationDb, "--json"]);

            Assert.Equal(CommandExitCodes.Success, importExit);
            Assert.Equal("automatic", importJson.GetProperty("backup_policy").GetString());
            var backupId = importJson.GetProperty("backup_id").GetString();
            Assert.False(string.IsNullOrWhiteSpace(backupId));
            Assert.Equal("src/Imported.cs", ReadFirstIndexedPath(destinationDb));

            var (listExit, listJson) = RunDbJson(
                ["restore-backups", "--list", "--db", destinationDb, "--json"]);
            Assert.Equal(CommandExitCodes.Success, listExit);
            var listed = Assert.Single(listJson.GetProperty("backups").EnumerateArray());
            Assert.Equal(backupId, listed.GetProperty("id").GetString());
            Assert.True(listed.GetProperty("managed").GetBoolean());
            Assert.Equal("pre_import", listed.GetProperty("provenance").GetString());
            var firstBackupPath = Assert.Single(EnumerateBackupDirectories(destinationDb));
            var manifestText = File.ReadAllText(Path.Combine(firstBackupPath, "manifest.json"));
            Assert.DoesNotContain(Path.GetFullPath(root), manifestText, StringComparison.Ordinal);

            var beforeDryRun = File.ReadAllBytes(destinationDb);
            var backupCountBeforeDryRun = EnumerateBackupDirectories(destinationDb).Length;
            var (dryRunExit, dryRunJson) = RunDbJson(
                ["restore-backups", "--restore", backupId!, "--dry-run", "--db", destinationDb, "--json"]);

            Assert.Equal(CommandExitCodes.Success, dryRunExit);
            Assert.Equal("dry_run", dryRunJson.GetProperty("status").GetString());
            Assert.True(dryRunJson.GetProperty("ready").GetBoolean());
            Assert.True(dryRunJson.GetProperty("manifest_valid").GetBoolean());
            Assert.True(dryRunJson.GetProperty("hash_valid").GetBoolean());
            Assert.True(dryRunJson.GetProperty("schema_valid").GetBoolean());
            Assert.True(dryRunJson.GetProperty("space_sufficient").GetBoolean());
            Assert.True(dryRunJson.GetProperty("pre_restore_backup_would_be_created").GetBoolean());
            Assert.Equal(beforeDryRun, File.ReadAllBytes(destinationDb));
            Assert.Equal(backupCountBeforeDryRun, EnumerateBackupDirectories(destinationDb).Length);

            var (restoreExit, restoreJson) = RunDbJson(
                ["restore-backups", "--restore", backupId!, "--db", destinationDb, "--json"]);

            Assert.Equal(CommandExitCodes.Success, restoreExit);
            Assert.Equal("success", restoreJson.GetProperty("status").GetString());
            Assert.True(restoreJson.GetProperty("restored").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                restoreJson.GetProperty("pre_restore_backup_id").GetString()));
            Assert.Equal("src/Original.cs", ReadFirstIndexedPath(destinationDb));
            Assert.Equal(2, EnumerateBackupDirectories(destinationDb).Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ImportNoBackupExplicitlySkipsManagedRollbackMaterial()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_issue4857_import_no_backup");
        try
        {
            var sourceDb = CreateDatabase(root, "source", "src/Imported.cs");
            var destinationDb = CreateDatabase(root, "destination", "src/Original.cs");
            var archivePath = ExportArchive(root, sourceDb);

            var (exitCode, json) = RunImportJson(
                [archivePath, "--db", destinationDb, "--no-backup", "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("disabled", json.GetProperty("backup_policy").GetString());
            Assert.True(
                !json.TryGetProperty("backup_id", out var backupId)
                || backupId.ValueKind is JsonValueKind.Null);
            Assert.Empty(EnumerateBackupDirectories(destinationDb));
            Assert.Equal("src/Imported.cs", ReadFirstIndexedPath(destinationDb));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ImportReplacementFailureRollsBackOriginalAndKeepsVerifiedBackup()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_issue4857_import_failure");
        try
        {
            var sourceDb = CreateDatabase(root, "source", "src/Imported.cs");
            var destinationDb = CreateDatabase(root, "destination", "src/Original.cs");
            var archivePath = ExportArchive(root, sourceDb);
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = _ =>
                throw new IOException("simulated post-move failure");

            var (exitCode, json) = RunImportJson(
                [archivePath, "--db", destinationDb, "--json"]);

            Assert.NotEqual(CommandExitCodes.Success, exitCode);
            Assert.Equal("replace_db", json.GetProperty("phase").GetString());
            Assert.Equal("import_replacement_failed", json.GetProperty("error_code").GetString());
            Assert.Equal("src/Original.cs", ReadFirstIndexedPath(destinationDb));
            var backupPath = Assert.Single(EnumerateBackupDirectories(destinationDb));
            Assert.True(File.Exists(Path.Combine(backupPath, "manifest.json")));

            var (listExit, listJson) = RunDbJson(
                ["restore-backups", "--list", "--db", destinationDb, "--json"]);
            Assert.Equal(CommandExitCodes.Success, listExit);
            Assert.True(Assert.Single(listJson.GetProperty("backups").EnumerateArray())
                .GetProperty("managed")
                .GetBoolean());
        }
        finally
        {
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = null;
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void RestoreDryRunRejectsCorruptPayloadWithoutMutatingDestination()
    {
        var fixture = CreateImportedFixture("cdidx_issue4857_corrupt_backup");
        try
        {
            var backupPath = Assert.Single(EnumerateBackupDirectories(fixture.DestinationDb));
            var payloadPath = Path.Combine(backupPath, Path.GetFileName(fixture.DestinationDb));
            File.AppendAllText(payloadPath, "corrupt");
            var before = File.ReadAllBytes(fixture.DestinationDb);

            var (exitCode, json) = RunDbJson(
                ["restore-backups", "--restore", fixture.BackupId, "--dry-run", "--db", fixture.DestinationDb, "--json"]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal("invalid", json.GetProperty("status").GetString());
            Assert.False(json.GetProperty("ready").GetBoolean());
            Assert.True(json.GetProperty("manifest_valid").GetBoolean());
            Assert.False(json.GetProperty("hash_valid").GetBoolean());
            Assert.Equal(before, File.ReadAllBytes(fixture.DestinationDb));
            Assert.Single(EnumerateBackupDirectories(fixture.DestinationDb));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void RestoreDryRunRejectsNullManifestHashWithoutUnhandledException()
    {
        var fixture = CreateImportedFixture("cdidx_issue4857_null_manifest_hash");
        try
        {
            var backupPath = Assert.Single(EnumerateBackupDirectories(fixture.DestinationDb));
            var manifestPath = Path.Combine(backupPath, "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["database_sha256"] = null;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var before = File.ReadAllBytes(fixture.DestinationDb);

            var (exitCode, json) = RunDbJson(
                ["restore-backups", "--restore", fixture.BackupId, "--dry-run", "--db", fixture.DestinationDb, "--json"]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal("invalid", json.GetProperty("status").GetString());
            Assert.False(json.GetProperty("ready").GetBoolean());
            Assert.False(json.GetProperty("manifest_valid").GetBoolean());
            Assert.Equal(before, File.ReadAllBytes(fixture.DestinationDb));
            Assert.Single(EnumerateBackupDirectories(fixture.DestinationDb));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void RestoreDryRunRejectsInsufficientSpaceWithoutMutatingDestination()
    {
        var fixture = CreateImportedFixture("cdidx_issue4857_restore_space");
        try
        {
            var before = File.ReadAllBytes(fixture.DestinationDb);
            DbCommandRunner.AvailableFreeSpaceForTesting = _ => 0;

            var (exitCode, json) = RunDbJson(
                ["restore-backups", "--restore", fixture.BackupId, "--dry-run", "--db", fixture.DestinationDb, "--json"]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.False(json.GetProperty("ready").GetBoolean());
            Assert.False(json.GetProperty("space_sufficient").GetBoolean());
            Assert.Equal(0, json.GetProperty("available_space_bytes").GetInt64());
            Assert.Equal(before, File.ReadAllBytes(fixture.DestinationDb));
            Assert.Single(EnumerateBackupDirectories(fixture.DestinationDb));
        }
        finally
        {
            DbCommandRunner.AvailableFreeSpaceForTesting = null;
            fixture.Dispose();
        }
    }

    [Fact]
    public void RestoreFailureAfterMoveRollsBackDestinationAndKeepsBothManagedBackups()
    {
        var fixture = CreateImportedFixture("cdidx_issue4857_restore_failure");
        try
        {
            var before = File.ReadAllBytes(fixture.DestinationDb);
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = _ =>
                throw new IOException("simulated restore installation failure");

            var (exitCode, json) = RunDbJson(
                ["restore-backups", "--restore", fixture.BackupId, "--db", fixture.DestinationDb, "--json"]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.False(json.GetProperty("restored").GetBoolean());
            Assert.Equal(before, File.ReadAllBytes(fixture.DestinationDb));
            Assert.Equal("src/Imported.cs", ReadFirstIndexedPath(fixture.DestinationDb));
            Assert.Equal(2, EnumerateBackupDirectories(fixture.DestinationDb).Length);
        }
        finally
        {
            ExportImportCommandRunner.ApplyPrivateFileModeForTesting = null;
            fixture.Dispose();
        }
    }

    [Fact]
    public void RestoreAcceptsManagedBackupWithOlderSupportedSchemaStamp()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_issue4857_old_schema");
        try
        {
            var sourceDb = CreateDatabase(root, "source", "src/Imported.cs");
            var destinationDb = CreateDatabase(root, "destination", "src/Original.cs");
            SetUserVersion(destinationDb, 0);
            var archivePath = ExportArchive(root, sourceDb);
            var (importExit, importJson) = RunImportJson(
                [archivePath, "--db", destinationDb, "--json"]);
            Assert.Equal(CommandExitCodes.Success, importExit);
            var backupId = importJson.GetProperty("backup_id").GetString()!;

            var (restoreExit, restoreJson) = RunDbJson(
                ["restore-backups", "--restore", backupId, "--db", destinationDb, "--json"]);

            Assert.Equal(CommandExitCodes.Success, restoreExit);
            Assert.True(restoreJson.GetProperty("schema_valid").GetBoolean());
            Assert.Equal(0, ReadUserVersion(destinationDb));
            Assert.Equal("src/Original.cs", ReadFirstIndexedPath(destinationDb));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ImportRejectsCorruptArchiveBeforeCreatingBackupOrReplacingDestination()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_issue4857_corrupt_import");
        try
        {
            var sourceDb = CreateDatabase(root, "source", "src/Imported.cs");
            var destinationDb = CreateDatabase(root, "destination", "src/Original.cs");
            var archivePath = ExportArchive(root, sourceDb);
            CorruptArchiveDatabaseEntry(archivePath);

            var (exitCode, json) = RunImportJson(
                [archivePath, "--db", destinationDb, "--json"]);

            Assert.NotEqual(CommandExitCodes.Success, exitCode);
            Assert.Equal("sha256", json.GetProperty("phase").GetString());
            Assert.Equal("src/Original.cs", ReadFirstIndexedPath(destinationDb));
            Assert.Empty(EnumerateBackupDirectories(destinationDb));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void RestoreOnWindowsRejectsExclusivelyLockedDestinationWithoutMutation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var fixture = CreateImportedFixture("cdidx_issue4857_windows_lock");
        try
        {
            var before = File.ReadAllBytes(fixture.DestinationDb);
            int exitCode;
            JsonElement json;
            using (var lockStream = new FileStream(
                       fixture.DestinationDb,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                (exitCode, json) = RunDbJson(
                    ["restore-backups", "--restore", fixture.BackupId, "--db", fixture.DestinationDb, "--json"]);
            }

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.False(json.GetProperty("restored").GetBoolean());
            Assert.Equal(before, File.ReadAllBytes(fixture.DestinationDb));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private ImportedFixture CreateImportedFixture(string name)
    {
        var root = TestProjectHelper.CreateTempProject(name);
        var sourceDb = CreateDatabase(root, "source", "src/Imported.cs");
        var destinationDb = CreateDatabase(root, "destination", "src/Original.cs");
        var archivePath = ExportArchive(root, sourceDb);
        var (exitCode, json) = RunImportJson(
            [archivePath, "--db", destinationDb, "--json"]);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        return new ImportedFixture(
            root,
            destinationDb,
            json.GetProperty("backup_id").GetString()!);
    }

    private static string CreateDatabase(string root, string directoryName, string indexedPath)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(Path.Combine(root, directoryName));
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            indexedPath,
            "csharp",
            "public class Fixture { }",
            releasePoolForFileAccess: true);
        return dbPath;
    }

    private static string ExportArchive(string root, string dbPath)
    {
        var archivePath = Path.Combine(root, $"issue4857-{Guid.NewGuid():N}.zip");
        var (exitCode, _, stderr) = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunExport(
                [archivePath, "--db", dbPath],
                new JsonSerializerOptions(),
                "test"));
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        return archivePath;
    }

    private (int ExitCode, JsonElement Json) RunImportJson(string[] args)
    {
        var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
            ExportImportCommandRunner.RunImport(args, _jsonOptions));
        Assert.Equal(string.Empty, stderr);
        return (exitCode, ParseJson(stdout));
    }

    private (int ExitCode, JsonElement Json) RunDbJson(string[] args)
    {
        var (exitCode, stdout, _) = ConsoleCapture.Capture(() =>
            DbCommandRunner.Run(args, _jsonOptions));
        return (exitCode, ParseJson(stdout));
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ReadFirstIndexedPath(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM files ORDER BY path LIMIT 1";
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static int ReadUserVersion(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void SetUserVersion(string dbPath, int version)
    {
        SqliteConnection.ClearAllPools();
        using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version={version}";
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static SqliteConnection OpenConnection(string dbPath, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = mode,
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static string[] EnumerateBackupDirectories(string dbPath)
    {
        var parent = Path.GetDirectoryName(dbPath)!;
        var prefix = Path.GetFileName(dbPath) + ".restore-backup-";
        return Directory.GetDirectories(parent)
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static void CorruptArchiveDatabaseEntry(string archivePath)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Update);
        var entry = archive.GetEntry("codeindex.db")
            ?? throw new InvalidOperationException("archive database entry is missing");
        byte[] bytes;
        using (var input = entry.Open())
        using (var buffer = new MemoryStream())
        {
            input.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        entry.Delete();
        var replacement = archive.CreateEntry("codeindex.db");
        using var output = replacement.Open();
        output.Write(bytes);
        output.WriteByte(0x42);
    }

    private sealed record ImportedFixture(
        string Root,
        string DestinationDb,
        string BackupId) : IDisposable
    {
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(Root);
        }
    }
}
