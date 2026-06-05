using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for `cdidx db` maintenance commands.
/// `cdidx db` 保守コマンドのテスト。
/// </summary>
[Collection("SQLite pool sensitive")]
public class DbCommandRunnerTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static IEnumerable<object[]> DirectSqliteModeArgs()
    {
        yield return new object[] { new[] { "--integrity-check" } };
        yield return new object[] { new[] { "schema" } };
        yield return new object[] { new[] { "prune", "--dry-run" } };
    }

    [Fact]
    public void ParseArgs_IntegrityCheckFlagSetsFlag()
    {
        var options = DbCommandRunner.ParseArgs(["--integrity-check"]);

        Assert.True(options.IntegrityCheck);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_SchemaSubcommandSetsFlag()
    {
        var options = DbCommandRunner.ParseArgs(["schema"]);

        Assert.True(options.Schema);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_PruneSubcommandSetsApplyFlag()
    {
        var options = DbCommandRunner.ParseArgs(["prune", "--apply"]);

        Assert.True(options.Prune);
        Assert.True(options.PruneApply);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_HelpFlagSetsShowHelp()
    {
        var options = DbCommandRunner.ParseArgs(["--help"]);

        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void ParseArgs_UnknownOptionRecordsParseError()
    {
        var options = DbCommandRunner.ParseArgs(["--bogus"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--bogus", options.ParseError);
    }

    [Fact]
    public void ParseArgs_PositionalArgRecordsParseError()
    {
        var options = DbCommandRunner.ParseArgs(["something"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("unknown db command", options.ParseError);
    }

    [Fact]
    public void ParseArgs_CheckpointCommandSetsFlagAndName()
    {
        var options = DbCommandRunner.ParseArgs(["checkpoint", "before-upgrade"]);

        Assert.True(options.Checkpoint);
        Assert.Equal("before-upgrade", options.Name);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_RestoreRequiresName()
    {
        var options = DbCommandRunner.ParseArgs(["restore"]);

        Assert.True(options.Restore);
        Assert.Contains("requires", options.ParseError);
    }

    [Fact]
    public void Run_WithoutModeFlag_ReturnsUsageError()
    {
        var (exitCode, _, stderr) = RunAndCaptureStreams([]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("db requires a mode flag", stderr);
        Assert.Contains("--integrity-check", stderr);
    }

    [Theory]
    [MemberData(nameof(DirectSqliteModeArgs))]
    public void Run_DirectSqliteModesRejectOversizedFileUriQuery_Issue3140(string[] modeArgs)
    {
        var dbUri = "file:///tmp/codeindex.db?" + new string('a', SqliteFileUri.MaxQueryLength + 1);
        var args = new List<string>(modeArgs) { "--db", dbUri }.ToArray();

        var (exitCode, stdout, stderr) = RunAndCaptureStreams(args);

        Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("invalid --db file URI", stderr);
        Assert.Contains($"SQLite file URI query length exceeds {SqliteFileUri.MaxQueryLength}", stderr);
        Assert.Contains("supported limits", stderr);
        Assert.DoesNotContain(new string('a', SqliteFileUri.MaxDiagnosticValueLength + 1), stderr);
    }

    [Fact]
    public void Run_WithUnknownOption_ReturnsUsageError()
    {
        var (exitCode, _, stderr) = RunAndCaptureStreams(["--bogus"]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--bogus", stderr);
    }

    [Fact]
    public void Run_MissingDb_ReturnsNotFoundWithHint()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_db_missing_{Guid.NewGuid():N}.db");

        var (exitCode, _, stderr) = RunAndCaptureStreams(["--integrity-check", "--db", missingDb]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Contains("database not found", stderr);
        Assert.Contains("cdidx index <projectPath>", stderr);
    }

    [Fact]
    public void Run_IntegrityCheck_FileUriSemicolonPayloadDoesNotCreateDatabase_Issue3220()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_db_uri_injection_{Guid.NewGuid():N}.db");
        var uri = new Uri(missingDb).AbsoluteUri + ";Mode=ReadWriteCreate";
        try
        {
            var (exitCode, _, stderr) = RunAndCaptureStreams(["--integrity-check", "--db", uri]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains("failed to run integrity check", stderr);
            Assert.False(File.Exists(missingDb));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(missingDb))
                File.Delete(missingDb);
        }
    }

    [Fact]
    public void Run_MissingDb_JsonShapeIncludesHint()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_db_missing_{Guid.NewGuid():N}.db");

        var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", missingDb, "--json"]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("database not found", json.GetProperty("message").GetString());
        Assert.Contains("cdidx index <projectPath>", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void Run_CleanDb_ReturnsOk()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_clean_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (exitCode, stdout, _) = RunAndCaptureStreams(["--integrity-check", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Integrity check", stdout);
            Assert.Contains("ok", stdout);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_CleanDb_JsonReportsOkTrueAndEmptyIssues()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_clean_json_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal(0, json.GetProperty("issues").GetArrayLength());
            Assert.Equal(Path.GetFullPath(dbPath), json.GetProperty("db_path").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_JsonIncludesTablesAndUserVersion()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_schema_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (exitCode, json) = RunAndCaptureJson(["schema", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(Path.GetFullPath(dbPath), json.GetProperty("db_path").GetString());
            Assert.True(json.TryGetProperty("user_version", out _));
            Assert.Contains(json.GetProperty("entries").EnumerateArray(), entry =>
                entry.GetProperty("type").GetString() == "table" &&
                entry.GetProperty("name").GetString() == "files");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_Prune_DryRunCountsAndApplyDeletesOrphans()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_prune_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SeedOrphans(dbPath);
            SqliteConnection.ClearAllPools();

            var (dryRunExit, dryRunJson) = RunAndCaptureJson(["prune", "--dry-run", "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, dryRunExit);
            Assert.True(dryRunJson.GetProperty("dry_run").GetBoolean());
            Assert.Equal(4, dryRunJson.GetProperty("total").GetInt32());

            var checkpointAttempted = false;
            DbContext.WalCheckpointTruncateExecutedForTesting = _ => checkpointAttempted = true;
            var (applyExit, applyJson) = RunAndCaptureJson(["prune", "--apply", "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, applyExit);
            Assert.False(applyJson.GetProperty("dry_run").GetBoolean());
            Assert.Equal(4, applyJson.GetProperty("total").GetInt32());
            Assert.True(checkpointAttempted);

            var (secondExit, secondJson) = RunAndCaptureJson(["prune", "--dry-run", "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, secondExit);
            Assert.Equal(0, secondJson.GetProperty("total").GetInt32());
        }
        finally
        {
            DbContext.WalCheckpointTruncateExecutedForTesting = null;
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_CheckpointAndRestore_RestoresDatabaseBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var originalBytes = File.ReadAllBytes(dbPath);
            var (checkpointExit, checkpointOut, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            Assert.Contains("saved", checkpointOut);

            File.WriteAllText(dbPath, "changed");

            var (restoreExit, restoreOut, _) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, restoreExit);
            Assert.Contains("Restored", restoreOut);
            Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
            Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_CheckpointRejectsOversizedNameBeforePathConstruction_Issue3124()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_name_cap_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        var name = new string('a', DbCommandRunner.MaxCheckpointNameLength + 1);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(dbPath, "db");

            var (exitCode, _, stderr) = RunAndCaptureStreams(["checkpoint", name, "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains($"checkpoint name is too long ({name.Length} characters; max {DbCommandRunner.MaxCheckpointNameLength})", stderr);
            Assert.Contains("truncated; original length", stderr);
            Assert.DoesNotContain(name, stderr);
            Assert.False(Directory.Exists(dbPath + ".checkpoints"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_Checkpoint_OnPosix_WritesPrivateSnapshotPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_private_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(dbPath, "db");
            File.WriteAllText(dbPath + "-wal", "wal");
            File.WriteAllText(dbPath + "-shm", "shm");

            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "private", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            var checkpointRoot = dbPath + ".checkpoints";
            var checkpointPath = Path.Combine(checkpointRoot, "private");
            AssertPrivateDirectory(checkpointRoot);
            AssertPrivateDirectory(checkpointPath);
            AssertPrivateFile(Path.Combine(checkpointPath, "codeindex.db"));
            AssertPrivateFile(Path.Combine(checkpointPath, "codeindex.db-wal"));
            AssertPrivateFile(Path.Combine(checkpointPath, "codeindex.db-shm"));
            AssertPrivateFile(Path.Combine(checkpointPath, "manifest.txt"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_CheckpointsList_JsonIncludesCreatedCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_list_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (checkpointExit, _) = RunAndCaptureJson(["checkpoint", "listed", "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            var (listExit, json) = RunAndCaptureJson(["checkpoints", "--list", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, listExit);
            var checkpoints = json.GetProperty("checkpoints");
            Assert.Single(checkpoints.EnumerateArray());
            Assert.Equal("listed", checkpoints[0].GetProperty("name").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_CheckpointsList_CapsCheckpointAndFileEnumeration_Issue2880()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_list_cap_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        var checkpointRoot = dbPath + ".checkpoints";
        Directory.CreateDirectory(checkpointRoot);
        try
        {
            File.WriteAllText(dbPath, "db");
            for (var i = 0; i < DbCommandRunner.CheckpointListEntryLimit + 1; i++)
            {
                var checkpointPath = Path.Combine(checkpointRoot, $"checkpoint-{i:D4}");
                Directory.CreateDirectory(checkpointPath);
                File.WriteAllText(Path.Combine(checkpointPath, "codeindex.db"), "db");
                for (var file = 0; file < DbCommandRunner.CheckpointFileInspectLimit + 1; file++)
                    File.WriteAllText(Path.Combine(checkpointPath, $"extra-{file:D4}.txt"), "x");
            }

            var (listExit, json) = RunAndCaptureJson(["checkpoints", "--list", "--db", dbPath, "--json"]);
            var (textExit, stdout, _) = RunAndCaptureStreams(["checkpoints", "--list", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, listExit);
            Assert.Equal(CommandExitCodes.Success, textExit);
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.Equal(DbCommandRunner.CheckpointListEntryLimit, json.GetProperty("checkpoint_limit").GetInt32());
            Assert.Equal(DbCommandRunner.CheckpointFileInspectLimit, json.GetProperty("file_limit").GetInt32());
            var checkpoints = json.GetProperty("checkpoints");
            Assert.Equal(DbCommandRunner.CheckpointListEntryLimit, checkpoints.GetArrayLength());
            Assert.Contains(checkpoints.EnumerateArray(), entry => entry.GetProperty("files_truncated").GetBoolean());
            Assert.Contains("truncated: yes", stdout);
            Assert.Contains("files truncated", stdout);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_RestoreIncompleteCheckpoint_ReturnsErrorAndKeepsDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_bad_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var originalBytes = File.ReadAllBytes(dbPath);
            var checkpointPath = Path.Combine(root, "codeindex.db.checkpoints", "bad");
            Directory.CreateDirectory(checkpointPath);
            File.WriteAllText(Path.Combine(checkpointPath, "manifest.txt"), "name=bad");

            var (restoreExit, _, stderr) = RunAndCaptureStreams(["restore", "bad", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Contains("incomplete", stderr);
            Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_RestoreFailureAfterBackup_RestoresOriginalDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_fail_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var originalBytes = File.ReadAllBytes(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            File.WriteAllText(dbPath, "changed");
            DbCommandRunner.RestoreFailureAfterBackupForTesting = () => throw new IOException("injected restore failure");

            var (restoreExit, _, stderr) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Contains("injected restore failure", stderr);
            Assert.Equal("changed", File.ReadAllText(dbPath));
            Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-tmp-*"));
        }
        finally
        {
            DbCommandRunner.RestoreFailureAfterBackupForTesting = null;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_Restore_OnPosix_CreatesPrivateStagingAndBackupPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_restore_private_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        var inspected = false;
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(dbPath, "original");
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            File.WriteAllText(dbPath, "changed");
            DbCommandRunner.RestoreFailureAfterBackupForTesting = () =>
            {
                var restoreTempPath = Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-tmp-*"));
                var backupPath = Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
                AssertPrivateDirectory(restoreTempPath);
                AssertPrivateDirectory(backupPath);
                AssertPrivateFile(Path.Combine(restoreTempPath, "codeindex.db"));
                AssertPrivateFile(Path.Combine(backupPath, "codeindex.db"));
                inspected = true;
            };

            var (restoreExit, _, _) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, restoreExit);
            Assert.True(inspected);
            AssertPrivateFile(dbPath);
            var finalBackupPath = Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
            AssertPrivateDirectory(finalBackupPath);
            AssertPrivateFile(Path.Combine(finalBackupPath, "codeindex.db"));
        }
        finally
        {
            DbCommandRunner.RestoreFailureAfterBackupForTesting = null;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_CorruptedDb_ReturnsDatabaseError()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_corrupt_{Guid.NewGuid():N}.db");
        try
        {
            // Write bytes that begin with a valid SQLite header so the file is recognized
            // as a database, then garbage after that triggers an integrity_check failure.
            // SQLite ヘッダで始めつつ後続をゴミにすることで integrity_check に検出させる。
            var header = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
            var bytes = new byte[4096];
            Array.Copy(header, bytes, header.Length);
            for (var i = header.Length; i < bytes.Length; i++)
                bytes[i] = 0xFF;
            File.WriteAllBytes(dbPath, bytes);

            var (exitCode, _, stderr) = RunAndCaptureStreams(["--integrity-check", "--db", dbPath]);

            // Either the pragma raises an exception (caught as DatabaseError) or it returns
            // non-"ok" rows; both paths must produce DatabaseError, never Success.
            // PRAGMA が例外を投げるか non-"ok" 行を返すかのいずれでも DatabaseError を返すべき。
            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.NotEmpty(stderr);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_IntegrityCheck_JsonCapsRowsAndText_Issue2881()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_integrity_cap_{Guid.NewGuid():N}.db");
        try
        {
            File.WriteAllText(dbPath, "placeholder");
            DbCommandRunner.IntegrityCheckRowsForTesting = () =>
                Enumerable.Range(0, DbCommandRunner.IntegrityCheckRowLimit + 1)
                    .Select(i => i == 0
                        ? new string('x', DbCommandRunner.IntegrityCheckTextLimit + 10)
                        : $"issue {i}");

            var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.True(json.GetProperty("rows_truncated").GetBoolean());
            Assert.True(json.GetProperty("text_truncated").GetBoolean());
            Assert.Equal(DbCommandRunner.IntegrityCheckRowLimit, json.GetProperty("row_limit").GetInt32());
            Assert.Equal(DbCommandRunner.IntegrityCheckTextLimit, json.GetProperty("text_limit").GetInt32());
            var issues = json.GetProperty("issues");
            Assert.Equal(DbCommandRunner.IntegrityCheckRowLimit, issues.GetArrayLength());
            Assert.EndsWith(" [truncated]", issues[0].GetString());
        }
        finally
        {
            DbCommandRunner.IntegrityCheckRowsForTesting = null;
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_JsonCapsEntriesAndSqlText_Issue2881()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_schema_cap_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ConnectionString))
            {
                connection.Open();
                var columns = string.Join(", ", Enumerable.Range(0, 900).Select(i => $"col{i:D4} TEXT"));
                Execute(connection, $"CREATE TABLE aaaa_long({columns})");
                for (var i = 0; i < DbCommandRunner.SchemaEntryLimit + 1; i++)
                    Execute(connection, $"CREATE TABLE t{i:D4}(value TEXT)");
            }
            SqliteConnection.ClearAllPools();

            var (exitCode, json) = RunAndCaptureJson(["schema", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.True(json.GetProperty("entries_truncated").GetBoolean());
            Assert.True(json.GetProperty("sql_truncated").GetBoolean());
            Assert.Equal(DbCommandRunner.SchemaEntryLimit, json.GetProperty("entry_limit").GetInt32());
            Assert.Equal(DbCommandRunner.SchemaSqlTextLimit, json.GetProperty("sql_text_limit").GetInt32());
            var entries = json.GetProperty("entries");
            Assert.Equal(DbCommandRunner.SchemaEntryLimit, entries.GetArrayLength());
            var longEntry = entries.EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == "aaaa_long");
            Assert.EndsWith(" [truncated]", longEntry.GetProperty("sql").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunAndCaptureStreams(string[] args)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = DbCommandRunner.RunIntegrityCheck(args, _jsonOptions);
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    private (int ExitCode, JsonElement Json) RunAndCaptureJson(string[] args)
    {
        using var capture = ConsoleCapture.Start(captureOut: true);
        var exitCode = DbCommandRunner.RunIntegrityCheck(args, _jsonOptions);
        using var document = JsonDocument.Parse(capture.Out!.ToString()!);
        return (exitCode, document.RootElement.Clone());
    }

    private static void AssertPrivateDirectory(string path)
    {
#pragma warning disable CA1416
        Assert.Equal(
            DataDirectorySecurity.PrivateDirectoryMode,
            File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits);
#pragma warning restore CA1416
    }

    private static void AssertPrivateFile(string path)
    {
#pragma warning disable CA1416
        Assert.Equal(
            DataDirectorySecurity.PrivateFileMode,
            File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits);
#pragma warning restore CA1416
    }

    private static void SeedOrphans(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=OFF";
            pragma.ExecuteNonQuery();
        }

        Execute(connection, "INSERT INTO symbols(file_id, kind, name, line) VALUES (9001, 'function', 'Orphan', 1)");
        Execute(connection, "INSERT INTO reference_lines(file_id, line, context) VALUES (9002, 1, 'missing file')");
        Execute(connection, "INSERT INTO symbol_references(file_id, symbol_name, reference_kind, reference_line_id) VALUES (9003, 'Orphan', 'call', 9004)");
        Execute(connection, "INSERT INTO files(id, path, lang, size, lines, modified, checksum) VALUES (1, 'src/live.cs', 'csharp', 1, 1, '2026-01-01T00:00:00Z', 'live')");
        Execute(connection, "INSERT INTO symbol_references(file_id, symbol_name, reference_kind, reference_line_id) VALUES (1, 'Live', 'call', 1)");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
