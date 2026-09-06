using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class ExportImportCommandRunnerIssue5274Tests
{
    private const string ExcludedCanary = "auditcanaryconfidential984721zz";
    private const string DeletedCanary = "auditcanarydeleted573192zz";
    private const string RetainedTerm = "publicauditsearchable";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedExport_RemovesRawResidualsAndPreservesSearchAndImport(bool legacyWithoutTrigram)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("export_fts_residuals_5274");
        var dbPath = CreateResidualFixture(project.Root, legacyWithoutTrigram);
        var originalBytes = File.ReadAllBytes(dbPath);
        Assert.True(originalBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(ExcludedCanary)) >= 0);
        Assert.True(originalBytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(DeletedCanary)) >= 0);

        // Default full export retains the complete source and does not sanitize its FTS.
        var fullArchive = Path.Combine(project.Root, "full.zip");
        Export(fullArchive, dbPath, []);
        var fullDb = ExtractDatabase(fullArchive, project.Root, "full.db");
        Assert.True(File.ReadAllBytes(fullDb).AsSpan().IndexOf(Encoding.ASCII.GetBytes(ExcludedCanary)) >= 0);
        using (var full = OpenReadOnly(fullDb))
        {
            Assert.Equal(5L, Scalar(full, "SELECT COUNT(*) FROM files"));
            Assert.Equal(legacyWithoutTrigram ? 0L : 1L,
                Scalar(full, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fts_chunks_trigram'"));
        }

        string[][] selectors =
        [
            ["--path", "src/App/Public.cs"],
            ["--lang", "cs"],
            ["--project", "App"],
            ["--exclude-path", "tests/*"],
            ["--exclude-tests"],
            ["--path", "missing/*"],
            ["--path", "*"],
        ];
        var variant = 0;
        foreach (var redact in new[] { false, true })
        {
            foreach (var selector in selectors)
            {
                var empty = selector.Contains("missing/*");
                var keepsAll = selector.Contains("*");
                var archive = Path.Combine(project.Root, $"scoped-{variant}.zip");
                var options = redact ? selector.Concat(["--redact-paths"]).ToArray() : selector;
                var manifest = Export(archive, dbPath, options);
                var extracted = ExtractDatabase(archive, project.Root, $"scoped-{variant}.db");
                var bytes = File.ReadAllBytes(extracted);
                if (!keepsAll)
                {
                    Assert.True(bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(ExcludedCanary)) < 0,
                        $"Excluded canary survived {string.Join(' ', options)} (legacy={legacyWithoutTrigram}).");
                    Assert.True(bytes.Length < new FileInfo(fullDb).Length, "Scoped DB should shrink relative to full export.");
                }
                Assert.True(bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(DeletedCanary)) < 0);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    manifest.GetProperty("database_sha256").GetString());
                Assert.Equal(empty ? 0 : keepsAll ? 5 : 1, manifest.GetProperty("file_count").GetInt64());
                Assert.False(manifest.GetProperty("index_complete").GetBoolean());
                Assert.Contains(manifest.GetProperty("index_incomplete_reasons").EnumerateArray(),
                    reason => reason.GetString() == "partial_archive");
                using (var connection = OpenReadOnly(extracted))
                {
                    Assert.Equal(0L, Scalar(connection, "PRAGMA freelist_count"));
                    string[] tables = legacyWithoutTrigram ? ["fts_chunks"] : ["fts_chunks", "fts_chunks_trigram"];
                    Assert.Equal(legacyWithoutTrigram ? 0L : 1L,
                        Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fts_chunks_trigram'"));
                    foreach (var table in tables)
                    {
                        Assert.Equal(empty ? 0L : 1L, Scalar(connection,
                            $"SELECT COUNT(*) FROM {table} WHERE {table} MATCH '{RetainedTerm}'"));
                        if (!keepsAll)
                            Assert.Equal(0L, Scalar(connection,
                                $"SELECT COUNT(*) FROM {table} WHERE {table} MATCH '{ExcludedCanary}'"));
                    }
                    if (!legacyWithoutTrigram)
                        Assert.Equal(empty ? 0L : 1L, Scalar(connection,
                            "SELECT COUNT(*) FROM fts_chunks_trigram WHERE fts_chunks_trigram MATCH 'auditsearch'"));
                }
                var importedDb = Path.Combine(project.Root, $"imported-{variant++}.db");
                var import = ConsoleCapture.Capture(() => ExportImportCommandRunner.RunImport(
                    [archive, "--db", importedDb, "--no-backup", "--json"], JsonOptions));
                Assert.True(import.ExitCode == CommandExitCodes.Success, import.Stdout + import.Stderr);
                Assert.Equal(string.Empty, import.Stderr);
                using (var imported = OpenReadOnly(importedDb))
                    Assert.Equal(empty ? 0L : 1L, Scalar(imported,
                        $"SELECT COUNT(*) FROM fts_chunks WHERE fts_chunks MATCH '{RetainedTerm}'"));
                Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(DataDirectorySecurity.PrivateFileMode,
                        File.GetUnixFileMode(archive) & DataDirectorySecurity.PermissionBits);
            }
        }
    }

    [Theory]
    [InlineData("fts_chunks", false)]
    [InlineData("fts_chunks_trigram", true)]
    public void ScopedExport_CancellationDuringSanitizationCannotPublish(string cancelAfterTable, bool redact)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("export_fts_cancel_5274");
        var dbPath = CreateResidualFixture(project.Root, false);
        var originalBytes = File.ReadAllBytes(dbPath);
        foreach (var overwrite in new[] { false, true })
        {
            var archive = Path.Combine(project.Root, overwrite ? "existing.zip" : "absent.zip");
            if (overwrite)
                File.WriteAllText(archive, "original destination");
            using var cancellation = new CancellationTokenSource();
            var reached = false;
            var previousHook = DbWriter.FtsTableRebuildStatementCompletedBeforeAutomergeRestoreForTesting;
            try
            {
                DbWriter.FtsTableRebuildStatementCompletedBeforeAutomergeRestoreForTesting = table =>
                {
                    if (table != cancelAfterTable)
                        return;
                    reached = true;
                    cancellation.Cancel();
                };
                var options = new List<string> { archive, "--db", dbPath, "--path", "src/App/*", "--json" };
                if (redact)
                    options.Add("--redact-paths");
                if (overwrite)
                    options.Add("--overwrite");
                var result = ConsoleCapture.Capture(() => ExportImportCommandRunner.RunExport(
                    options.ToArray(), JsonOptions, "test", cancellation.Token));
                Assert.True(reached);
                Assert.Equal(CommandExitCodes.CancelledBySignal, result.ExitCode);
                Assert.Equal(string.Empty, result.Stderr);
                using var json = JsonDocument.Parse(result.Stdout);
                Assert.Equal("scope_archive", json.RootElement.GetProperty("phase").GetString());
                if (overwrite)
                    Assert.Equal("original destination", File.ReadAllText(archive));
                else
                    Assert.False(File.Exists(archive));
                Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
                Assert.Empty(Directory.GetFiles(project.Root, ".cdidx-*.tmp"));
            }
            finally
            {
                DbWriter.FtsTableRebuildStatementCompletedBeforeAutomergeRestoreForTesting = previousHook;
            }
        }
    }

    private static string CreateResidualFixture(string root, bool legacyWithoutTrigram)
    {
        TestProjectHelper.WriteTextFile(root, "src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var dbPath = TestProjectHelper.CreateProjectDb(root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/App/Public.cs", "csharp", $"// {RetainedTerm}\npublic class Public {{ }}");
        using (var connection = OpenWritable(dbPath))
        {
            Execute(connection, "INSERT INTO fts_chunks(fts_chunks, rank) VALUES('automerge', 0)");
            Execute(connection, "INSERT INTO fts_chunks_trigram(fts_chunks_trigram, rank) VALUES('automerge', 0)");
        }
        // Separate commits preserve multiple segments, repeated terms, and deletion tombstones.
        for (var i = 0; i < 4; i++)
        {
            var terms = string.Join(' ', Enumerable.Range(0, i == 0 ? 0 : 256).Select(n =>
                Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"private-{i}-{n}"))).ToLowerInvariant()));
            TestProjectHelper.InsertIndexedFile(dbPath, $"tests/Private{i}.py", "python",
                $"# {ExcludedCanary} {ExcludedCanary} {terms}");
        }
        TestProjectHelper.InsertIndexedFile(dbPath, "tests/Deleted.py", "python", $"# {DeletedCanary}");
        SqliteConnection.ClearAllPools();
        using (var connection = OpenWritable(dbPath))
        {
            Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA secure_delete = OFF; DELETE FROM files WHERE path = 'tests/Deleted.py'");
            Assert.True(Scalar(connection, "SELECT COUNT(DISTINCT segid) FROM fts_chunks_idx") > 1);
            Assert.True(Scalar(connection, "SELECT COUNT(DISTINCT segid) FROM fts_chunks_trigram_idx") > 1);
            if (legacyWithoutTrigram)
                Execute(connection, """
                    DROP TRIGGER fts_chunks_trigram_ai;
                    DROP TRIGGER fts_chunks_trigram_ad;
                    DROP TRIGGER fts_chunks_trigram_au;
                    DROP TABLE fts_chunks_trigram;
                    """);
        }
        return dbPath;
    }

    private static JsonElement Export(string archive, string dbPath, string[] options)
    {
        var result = ConsoleCapture.Capture(() => ExportImportCommandRunner.RunExport(
            [archive, "--db", dbPath, "--json", .. options], JsonOptions, "test"));
        Assert.True(result.ExitCode == CommandExitCodes.Success, result.Stdout + result.Stderr);
        Assert.Equal(string.Empty, result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        return json.RootElement.GetProperty("manifest").Clone();
    }

    private static string ExtractDatabase(string archivePath, string root, string name)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var destination = Path.Combine(root, name);
        archive.GetEntry("codeindex.db")!.ExtractToFile(destination);
        return destination;
    }

    private static SqliteConnection OpenReadOnly(string path) => Open(path, SqliteOpenMode.ReadOnly);
    private static SqliteConnection OpenWritable(string path) => Open(path, SqliteOpenMode.ReadWrite);

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
