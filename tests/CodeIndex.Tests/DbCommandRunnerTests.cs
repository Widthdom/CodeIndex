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
    public void ParseArgs_RestoreBackupsPruneSetsKeep_Issue3833()
    {
        var options = DbCommandRunner.ParseArgs(["restore-backups", "--prune", "--keep", "3"]);

        Assert.True(options.RestoreBackups);
        Assert.True(options.RestoreBackupsPrune);
        Assert.Equal(3, options.RestoreBackupsKeep);
        Assert.Null(options.ParseError);
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
    public void Run_IntegrityCheck_FileUriJsonReportsUriWithoutPathNormalization_Issue3221()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_uri_display_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", dbUri, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(dbUri, json.GetProperty("db_path").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_FileUriHumanOutputReportsUriWithoutPathNormalization_Issue3221()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_uri_schema_display_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, _) = RunAndCaptureStreams(["schema", "--db", dbUri]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains($"database    : {dbUri}", stdout);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
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
            Assert.Equal("ok", json.GetProperty("severity").GetString());
            Assert.Equal("integrity_ok", json.GetProperty("diagnostic_code").GetString());
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
    public void Run_IntegrityCheck_JsonCancellationReturnsInterrupted_Issue3811()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_integrity_cancel_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var (exitCode, stdout, stderr) = RunAndCaptureStreams(["--integrity-check", "--db", dbPath, "--json"], cts.Token);

            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var json = document.RootElement;
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, json.GetProperty("error_code").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Run_IntegrityCheck_JsonReportsStableErrorSeverity()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_integrity_error_json_{Guid.NewGuid():N}.db");
        DbCommandRunner.IntegrityCheckRowsForTesting = () => ["simulated corruption"];
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.False(json.GetProperty("ok").GetBoolean());
            Assert.Equal("error", json.GetProperty("severity").GetString());
            Assert.Equal("integrity_failed", json.GetProperty("diagnostic_code").GetString());
            Assert.Equal("simulated corruption", json.GetProperty("issues")[0].GetString());
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
            Assert.Equal("ok", json.GetProperty("severity").GetString());
            Assert.Equal("schema_ok", json.GetProperty("diagnostic_code").GetString());
            Assert.True(json.GetProperty("object_type_counts").GetProperty("table").GetInt32() > 0);
            Assert.Equal(0, json.GetProperty("object_type_omitted_counts").GetProperty("table").GetInt32());
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
    public void Run_Schema_JsonReportsObjectTypeOmissionsWhenEntryLimitTruncates()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_schema_truncated_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                for (var i = 0; i < DbCommandRunner.SchemaEntryLimit + 5; i++)
                    Execute(connection, $"CREATE TABLE t_{i:D3}(id INTEGER PRIMARY KEY);");
            }
            SqliteConnection.ClearAllPools();

            var (exitCode, json) = RunAndCaptureJson(["schema", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("warn", json.GetProperty("severity").GetString());
            Assert.Equal("schema_truncated", json.GetProperty("diagnostic_code").GetString());
            Assert.True(json.GetProperty("entries_truncated").GetBoolean());
            Assert.Equal(DbCommandRunner.SchemaEntryLimit + 5, json.GetProperty("object_type_counts").GetProperty("table").GetInt32());
            Assert.Equal(5, json.GetProperty("object_type_omitted_counts").GetProperty("table").GetInt32());
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
    public void Run_PruneApply_JsonReportsWalCheckpointWarning_Issue3514()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_prune_wal_warning_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SeedOrphans(dbPath);
            SqliteConnection.ClearAllPools();
            DbContext.WalCheckpointTruncateExecutedForTesting = _ => throw new IOException("simulated wal cleanup failure");

            var (exitCode, json) = RunAndCaptureJson(["prune", "--apply", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var warnings = json.GetProperty("warnings");
            var warning = Assert.Single(warnings.EnumerateArray());
            Assert.Equal("wal_checkpoint_truncate_failed", warning.GetProperty("code").GetString());
            Assert.Contains("WAL checkpoint truncation failed", warning.GetProperty("message").GetString());
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
    public void Run_PruneDryRun_ReportsMaintenanceProgress_Issue3811()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_prune_progress_{Guid.NewGuid():N}.db");
        var progress = new List<string>();
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SeedOrphans(dbPath);
            SqliteConnection.ClearAllPools();
            DbCommandRunner.MaintenanceProgressForTesting = (operation, phase) => progress.Add($"{operation}:{phase}");

            var (exitCode, json) = RunAndCaptureJson(["prune", "--dry-run", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(4, json.GetProperty("total").GetInt32());
            Assert.Contains("prune:start", progress);
            Assert.Contains("prune:count_symbol_references", progress);
            Assert.Contains("prune:count_reference_lines", progress);
            Assert.Contains("prune:count_symbols", progress);
            Assert.Contains("prune:complete", progress);
        }
        finally
        {
            DbCommandRunner.MaintenanceProgressForTesting = null;
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
    public void Run_CheckpointManifestOmitsAbsoluteDbPath_Issue3833()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_manifest_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(dbPath, "db");

            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "manifest", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            var manifest = File.ReadAllText(Path.Combine(dbPath + ".checkpoints", "manifest", "manifest.txt"));
            Assert.Contains("db_file=codeindex.db", manifest);
            Assert.DoesNotContain(dbPath, manifest);
            Assert.DoesNotContain(root, manifest);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_CheckpointJsonSuccessKeepsDiagnosticsArray_Issue3812()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_json_contract_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(dbPath, "db");

            var (checkpointExit, json) = RunAndCaptureJson(["checkpoint", "contract", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            var diagnostics = json.GetProperty("diagnostics");
            Assert.Equal(JsonValueKind.Array, diagnostics.ValueKind);
            Assert.Equal(0, diagnostics.GetArrayLength());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_CheckpointInjectedClockControlsNameAndManifest_Issue3963()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_clock_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        var fixedTime = new DateTimeOffset(2026, 6, 23, 4, 5, 6, 789, TimeSpan.Zero);
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(dbPath, "db");
            DbCommandRunner.UtcNowForTesting = () => fixedTime;

            var (checkpointExit, json) = RunAndCaptureJson(["checkpoint", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            Assert.Equal("20260623040506789", json.GetProperty("name").GetString());
            var checkpointPath = json.GetProperty("checkpoint_path").GetString();
            Assert.NotNull(checkpointPath);
            var manifest = File.ReadAllText(Path.Combine(checkpointPath, "manifest.txt"));
            Assert.Contains($"created_at_utc={fixedTime:O}", manifest, StringComparison.Ordinal);
        }
        finally
        {
            DbCommandRunner.UtcNowForTesting = null;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_CheckpointJsonReportsRecoverableFileNameEnumerationFailure_Issue3833()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_enum_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(dbPath, "db");
            DbCommandRunner.EnumerateCheckpointFileNamesForTesting = _ => throw new IOException("secret local enumeration path");

            var (checkpointExit, json) = RunAndCaptureJson(["checkpoint", "enum", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            Assert.True(json.GetProperty("files_truncated").GetBoolean());
            var diagnostic = Assert.Single(json.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal("checkpoint_file_enumeration_failed", diagnostic.GetProperty("code").GetString());
            Assert.DoesNotContain("secret local enumeration path", json.ToString());
        }
        finally
        {
            DbCommandRunner.EnumerateCheckpointFileNamesForTesting = null;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_Checkpoint_JsonReportsFileEnumerationDiagnostic_Issue3812()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_file_enum_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(dbPath, "db");
            DbCommandRunner.EnumerateCheckpointFilesForTesting = _ => throw new UnauthorizedAccessException("checkpoint file enumeration denied");

            var (checkpointExit, stdout, stderr) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            using var doc = JsonDocument.Parse(stdout);
            var rootElement = doc.RootElement;
            Assert.Equal("success", rootElement.GetProperty("status").GetString());
            Assert.True(rootElement.GetProperty("files_truncated").GetBoolean());
            var diagnostic = Assert.Single(rootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal("checkpoint_file_enumeration_failed", diagnostic.GetProperty("code").GetString());
            Assert.Contains("Unable to enumerate every checkpoint file", diagnostic.GetProperty("message").GetString());
            Assert.Contains("Warning [checkpoint_file_enumeration_failed]", stderr);
        }
        finally
        {
            DbCommandRunner.EnumerateCheckpointFilesForTesting = null;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_CheckpointTempCleanupFailurePreservesOriginalFailure_Issue3029()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_cleanup_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        string? cleanupPath = null;
        try
        {
            Directory.CreateDirectory(root);
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var checkpointRoot = dbPath + ".checkpoints";
            Directory.CreateDirectory(checkpointRoot);
            File.WriteAllText(Path.Combine(checkpointRoot, "saved"), "checkpoint path blocker");
            DbCommandRunner.DeleteTemporaryDirectoryForTesting = path =>
            {
                cleanupPath = path;
                throw new IOException("simulated checkpoint temp cleanup failure");
            };

            var (exitCode, _, stderr) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains("failed to create database checkpoint", stderr);
            Assert.Contains("Warning: failed to delete checkpoint temporary directory", stderr);
            Assert.Contains("IOException", stderr);
            Assert.NotNull(cleanupPath);
            Assert.True(Directory.Exists(cleanupPath));
        }
        finally
        {
            DbCommandRunner.DeleteTemporaryDirectoryForTesting = null;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryDeleteTemporaryDirectory_RejectsTargetOutsideSafeRoot_Issue3379()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_cleanup_safe_root_{Guid.NewGuid():N}");
        var safeRoot = Path.Combine(root, "safe");
        var outsideRoot = Path.Combine(root, "outside");
        var outsideTarget = Path.Combine(outsideRoot, ".tmp-malformed");
        try
        {
            Directory.CreateDirectory(safeRoot);
            Directory.CreateDirectory(outsideTarget);
            File.WriteAllText(Path.Combine(outsideTarget, "sentinel.txt"), "keep");

            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                DbCommandRunner.TryDeleteTemporaryDirectory(
                    outsideTarget,
                    "test temporary directory",
                    safeRoot,
                    ".tmp-");
                return 0;
            });

            Assert.True(Directory.Exists(outsideTarget));
            Assert.Contains("skipped deleting test temporary directory", stderr);
            Assert.Contains("outside the expected cleanup root", stderr);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryDeleteTemporaryDirectory_RejectsReparseCleanupTarget_Issue3732()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_cleanup_reparse_{Guid.NewGuid():N}");
        var safeRoot = Path.Combine(root, "safe");
        var outsideTarget = Path.Combine(root, "outside-target");
        var cleanupTarget = Path.Combine(safeRoot, ".tmp-linked");
        try
        {
            Directory.CreateDirectory(safeRoot);
            Directory.CreateDirectory(outsideTarget);
            File.WriteAllText(Path.Combine(outsideTarget, "sentinel.txt"), "keep");
            Directory.CreateSymbolicLink(cleanupTarget, outsideTarget);

            var (_, _, stderr) = ConsoleCapture.Capture(() =>
            {
                DbCommandRunner.TryDeleteTemporaryDirectory(
                    cleanupTarget,
                    "test temporary directory",
                    safeRoot,
                    ".tmp-");
                return 0;
            });

            Assert.True(Directory.Exists(outsideTarget));
            Assert.True(Directory.Exists(cleanupTarget));
            Assert.Contains("skipped deleting test temporary directory", stderr);
            Assert.Contains("not a regular temporary directory", stderr);
        }
        finally
        {
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
            Assert.Contains("InvalidOperationException", stderr);
            Assert.DoesNotContain("checkpoint is incomplete", stderr);
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
            Assert.Contains("IOException", stderr);
            Assert.DoesNotContain("injected restore failure", stderr);
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
    public void Run_RestoreRollbackFailurePreservesPrimaryFailure_Issue3514()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_rollback_fail_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            File.WriteAllText(dbPath, "changed");
            DbCommandRunner.RestoreFailureAfterBackupForTesting = () =>
            {
                Directory.CreateDirectory(dbPath);
                throw new IOException("primary restore failure");
            };

            var (restoreExit, _, stderr) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Contains("failed to restore database checkpoint", stderr);
            Assert.Contains("IOException", stderr);
            Assert.DoesNotContain("primary restore failure", stderr);
            Assert.Contains("Failed to roll back database restore", stderr);
            var backupPath = Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
            Assert.True(File.Exists(Path.Combine(backupPath, "codeindex.db")));
            Assert.True(Directory.Exists(dbPath));
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
    public void Run_RestoreRollbackFailureJsonIncludesStructuredMetadata_Issue3833()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_rollback_json_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            File.WriteAllText(dbPath, "changed");
            DbCommandRunner.RestoreFailureAfterBackupForTesting = () =>
            {
                Directory.CreateDirectory(dbPath);
                throw new IOException("primary restore failure token=secret");
            };

            var (restoreExit, stdout, _) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath, "--json"]);
            using var document = JsonDocument.Parse(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("rollback_failed").GetBoolean());
            Assert.Equal("restore_rollback_failed", json.GetProperty("rollback_failure").GetProperty("code").GetString());
            Assert.Contains("IOException", json.GetProperty("message").GetString());
            Assert.DoesNotContain("primary restore failure", stdout);
            Assert.DoesNotContain("token=secret", stdout);
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
    public void Run_RestoreBackupsListAndPruneOrdersByRecency_Issue3833()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_restore_backups_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(dbPath, "current");
            var older = Path.Combine(root, "codeindex.db.restore-backup-20260101000000000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var newer = Path.Combine(root, "codeindex.db.restore-backup-20260102000000000-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            Directory.CreateDirectory(older);
            Directory.CreateDirectory(newer);
            File.WriteAllText(Path.Combine(older, "codeindex.db"), "older");
            File.WriteAllText(Path.Combine(newer, "codeindex.db"), "newer");
            var sameCreationTime = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
            Directory.SetCreationTimeUtc(older, sameCreationTime);
            Directory.SetCreationTimeUtc(newer, sameCreationTime);

            var (listExit, listJson) = RunAndCaptureJson(["restore-backups", "--list", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, listExit);
            var backups = listJson.GetProperty("backups");
            Assert.Equal(2, backups.GetArrayLength());
            Assert.Equal(Path.GetFileName(newer), backups[0].GetProperty("name").GetString());

            var (pruneExit, pruneJson) = RunAndCaptureJson(["restore-backups", "--prune", "--keep", "1", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, pruneExit);
            Assert.Equal(1, pruneJson.GetProperty("deleted").GetInt32());
            Assert.Equal(1, pruneJson.GetProperty("retained").GetInt32());
            Assert.False(Directory.Exists(older));
            Assert.True(Directory.Exists(newer));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_RestoreBackupsRejectsDryRunWithoutDeleting_Issue3833()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_restore_backups_dry_run_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(dbPath, "current");
            var backup = Path.Combine(root, "codeindex.db.restore-backup-20260101000000000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "codeindex.db"), "backup");

            var (exitCode, _, stderr) = RunAndCaptureStreams(["restore-backups", "--prune", "--dry-run", "--keep", "0", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains("--dry-run and --apply are not supported", stderr);
            Assert.True(Directory.Exists(backup));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_RestoreBackupsPruneSkipsDeletionWhenScanTruncated_Issue3833()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_restore_backups_truncated_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(dbPath, "current");
            for (var i = 0; i < DbCommandRunner.RestoreBackupPruneScanLimit + 1; i++)
            {
                var backup = Path.Combine(root, $"codeindex.db.restore-backup-20260101000000000-{i:x32}");
                Directory.CreateDirectory(backup);
                File.WriteAllText(Path.Combine(backup, "codeindex.db"), "backup");
            }

            var (exitCode, json) = RunAndCaptureJson(["restore-backups", "--prune", "--keep", "0", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.Equal(0, json.GetProperty("deleted").GetInt32());
            Assert.Equal(DbCommandRunner.RestoreBackupPruneScanLimit, json.GetProperty("retained").GetInt32());
            Assert.Contains(
                json.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString() == "restore_backup_prune_truncated");
            Assert.Equal(
                DbCommandRunner.RestoreBackupPruneScanLimit + 1,
                Directory.GetDirectories(root, "codeindex.db.restore-backup-*").Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_RestoreBackupsPruneDeletesWhenOnlyFileInspectionTruncated_Issue3833()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_restore_backups_file_truncated_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(dbPath, "current");
            var older = Path.Combine(root, "codeindex.db.restore-backup-20260101000000000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var newer = Path.Combine(root, "codeindex.db.restore-backup-20260102000000000-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            Directory.CreateDirectory(older);
            Directory.CreateDirectory(newer);
            File.WriteAllText(Path.Combine(older, "codeindex.db"), "older");
            File.WriteAllText(Path.Combine(newer, "codeindex.db"), "newer");
            for (var i = 0; i < DbCommandRunner.CheckpointFileInspectLimit + 1; i++)
                File.WriteAllText(Path.Combine(newer, $"extra-{i:D4}.txt"), "x");

            var (exitCode, json) = RunAndCaptureJson(["restore-backups", "--prune", "--keep", "1", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.Equal(1, json.GetProperty("deleted").GetInt32());
            Assert.Equal(1, json.GetProperty("retained").GetInt32());
            Assert.False(Directory.Exists(older));
            Assert.True(Directory.Exists(newer));
            if (json.TryGetProperty("diagnostics", out var diagnostics))
            {
                Assert.DoesNotContain(
                    diagnostics.EnumerateArray(),
                    diagnostic => diagnostic.GetProperty("code").GetString() == "restore_backup_prune_truncated");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_RestoreRejectsSymlinkedCheckpointPayload_Issue3514()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_symlink_{Guid.NewGuid():N}");
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

            var checkpointDbPath = Path.Combine(dbPath + ".checkpoints", "saved", "codeindex.db");
            File.Delete(checkpointDbPath);
            var targetPath = Path.Combine(root, "payload-target.db");
            File.WriteAllText(targetPath, "not the checkpoint");
            File.CreateSymbolicLink(checkpointDbPath, targetPath);

            var (restoreExit, _, stderr) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Contains("InvalidOperationException", stderr);
            Assert.DoesNotContain("not a regular file", stderr);
            Assert.DoesNotContain(checkpointDbPath, stderr);
            Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_RestoreRejectsSymlinkedCheckpointSidecar_Issue3812()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_checkpoint_sidecar_symlink_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();
            File.WriteAllText(dbPath + "-wal", "wal");
            File.WriteAllText(dbPath + "-shm", "shm");
            var originalBytes = File.ReadAllBytes(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            var checkpointWalPath = Path.Combine(dbPath + ".checkpoints", "saved", "codeindex.db-wal");
            File.Delete(checkpointWalPath);
            var targetPath = Path.Combine(root, "sidecar-target.wal");
            File.WriteAllText(targetPath, "not the checkpoint wal");
            File.CreateSymbolicLink(checkpointWalPath, targetPath);

            var (restoreExit, _, stderr) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Contains("failed to restore database checkpoint", stderr);
            Assert.Contains("InvalidOperationException", stderr);
            Assert.DoesNotContain("not a regular file", stderr);
            Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Run_RestoreTemporaryNamesIncludeCollisionResistantSuffix_Issue3031()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_restore_suffix_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        var inspected = false;
        Directory.CreateDirectory(root);
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            File.WriteAllText(dbPath, "changed");
            DbCommandRunner.RestoreFailureAfterBackupForTesting = () =>
            {
                var restoreTempPath = Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-tmp-*"));
                var backupPath = Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
                AssertRestoreSuffix(Path.GetFileName(restoreTempPath), "codeindex.db.restore-tmp-");
                AssertRestoreSuffix(Path.GetFileName(backupPath), "codeindex.db.restore-backup-");
                inspected = true;
            };

            var (restoreExit, _, _) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, restoreExit);
            Assert.True(inspected);
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
    public void Run_RestoreTempCleanupFailureWarnsWithoutFailing_Issue3030()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdidx_db_restore_cleanup_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "codeindex.db");
        string? cleanupPath = null;
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
            DbCommandRunner.DeleteTemporaryDirectoryForTesting = path =>
            {
                if (Path.GetFileName(path).StartsWith("codeindex.db.restore-tmp-", StringComparison.Ordinal))
                {
                    cleanupPath = path;
                    throw new IOException("simulated restore temp cleanup failure");
                }

                Directory.Delete(path, recursive: true);
            };

            var (restoreExit, restoreOut, stderr) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, restoreExit);
            Assert.Contains("Restored", restoreOut);
            Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
            Assert.Contains("Warning: failed to delete restore temporary directory", stderr);
            Assert.Contains("IOException", stderr);
            Assert.NotNull(cleanupPath);
            Assert.True(Directory.Exists(cleanupPath));
        }
        finally
        {
            DbCommandRunner.DeleteTemporaryDirectoryForTesting = null;
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

    private (int ExitCode, string StdOut, string StdErr) RunAndCaptureStreams(string[] args, CancellationToken cancellationToken = default)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = DbCommandRunner.Run(args, _jsonOptions, cancellationToken);
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    private (int ExitCode, JsonElement Json) RunAndCaptureJson(string[] args, CancellationToken cancellationToken = default)
    {
        using var capture = ConsoleCapture.Start(captureOut: true);
        var exitCode = DbCommandRunner.Run(args, _jsonOptions, cancellationToken);
        using var document = JsonDocument.Parse(capture.Out!.ToString()!);
        return (exitCode, document.RootElement.Clone());
    }

    private static void AssertRestoreSuffix(string directoryName, string prefix)
    {
        Assert.StartsWith(prefix, directoryName);
        var suffix = directoryName[prefix.Length..];
        Assert.Equal(50, suffix.Length);
        Assert.True(suffix[..17].All(char.IsDigit));
        Assert.Equal('-', suffix[17]);
        Assert.True(suffix[18..].All(char.IsAsciiHexDigit));
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
