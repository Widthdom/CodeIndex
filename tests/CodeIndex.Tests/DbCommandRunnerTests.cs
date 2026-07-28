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
        yield return new object[] { new[] { "integrity" } };
        yield return new object[] { new[] { "schema" } };
        yield return new object[] { new[] { "prune", "--dry-run" } };
    }

    private static void InitializeEmptyDb(string dbPath)
    {
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            db.InitializeSchema();
        ReleaseSqlitePools();
    }

    private static void InitializeDbWithOrphans(string dbPath)
    {
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            db.InitializeSchema();
        SeedOrphans(dbPath);
        ReleaseSqlitePools();
    }

    private static void ReleaseSqlitePools()
        => SqliteConnection.ClearAllPools();

    private static void DeleteDbFile(string dbPath)
    {
        ReleaseSqlitePools();
        TestProjectHelper.DeleteFile(dbPath);
    }

    private static void DeleteWorkDirectory(string root)
    {
        ReleaseSqlitePools();
        TestProjectHelper.DeleteDirectory(root);
    }

    [Fact]
    public void ParseArgs_IntegrityCheckFlagSetsFlag()
    {
        var options = DbCommandRunner.ParseArgs(["--integrity-check"]);

        Assert.True(options.IntegrityCheck);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_IntegritySubcommandSetsFlag_Issue3958()
    {
        var options = DbCommandRunner.ParseArgs(["integrity"]);

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
    public void ParseArgs_SchemaProjectionOptionsSetFilters_Issue3958()
    {
        var options = DbCommandRunner.ParseArgs(["schema", "--type", "TABLE", "--name", "files", "--summary-only"]);

        Assert.True(options.Schema);
        Assert.Equal("table", options.SchemaType);
        Assert.Equal("files", options.SchemaName);
        Assert.True(options.SchemaSummaryOnly);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_SchemaProjectionOptionsRequireSchema_Issue3958()
    {
        var options = DbCommandRunner.ParseArgs(["integrity", "--summary-only"]);

        Assert.Contains("only valid", options.ParseError);
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
    public void ParseArgs_CheckpointDryRunSetsPreviewFlag_Issue3937()
    {
        var options = DbCommandRunner.ParseArgs(["checkpoint", "before-upgrade", "--dry-run"]);

        Assert.True(options.Checkpoint);
        Assert.True(options.CheckpointDryRun);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_SchemaSizeControlsSetLimits_Issue3937()
    {
        var options = DbCommandRunner.ParseArgs(["schema", "--limit", "3", "--max-sql-chars", "40", "--exclude-internal"]);

        Assert.True(options.Schema);
        Assert.Equal(3, options.SchemaEntryLimit);
        Assert.Equal(40, options.SchemaSqlTextLimit);
        Assert.False(options.SchemaIncludeInternal);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_ApplyRequiresPrune_Issue3937()
    {
        var options = DbCommandRunner.ParseArgs(["checkpoint", "--apply"]);

        Assert.Contains("only valid", options.ParseError);
    }

    [Fact]
    public void ParseArgs_RestoreRequiresName()
    {
        var options = DbCommandRunner.ParseArgs(["restore"]);

        Assert.True(options.Restore);
        Assert.Contains("requires", options.ParseError);
    }

    [Fact]
    public void ParseArgs_RestoreAndCheckpointCleanupDryRunsSetPreviewFlags_Issue4717()
    {
        var restore = DbCommandRunner.ParseArgs(["restore", "saved", "--dry-run"]);
        var delete = DbCommandRunner.ParseArgs(["checkpoints", "--delete", "saved", "--dry-run"]);
        var missingDeleteName = DbCommandRunner.ParseArgs(["checkpoints", "--delete", "--dry-run"]);
        var prune = DbCommandRunner.ParseArgs(["checkpoints", "--prune", "--keep", "3", "--dry-run"]);

        Assert.True(restore.RestoreDryRun);
        Assert.True(delete.CheckpointsDelete);
        Assert.True(delete.CheckpointsDryRun);
        Assert.Equal("saved", delete.Name);
        Assert.Contains("requires a checkpoint name", missingDeleteName.ParseError);
        Assert.True(prune.CheckpointsPrune);
        Assert.True(prune.CheckpointsDryRun);
        Assert.Equal(3, prune.CheckpointsKeep);
        Assert.Null(restore.ParseError);
        Assert.Null(delete.ParseError);
        Assert.Null(prune.ParseError);
    }

    [Fact]
    public void ParseArgs_RestoreBackupsPruneSetsKeep_Issue3833()
    {
        var options = DbCommandRunner.ParseArgs(["restore-backups", "--prune", "--keep", "3", "--dry-run"]);

        Assert.True(options.RestoreBackups);
        Assert.True(options.RestoreBackupsPrune);
        Assert.True(options.RestoreBackupsDryRun);
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
        var missingDb = TestProjectHelper.CreateTempDbPath("cdidx_db_missing");

        var (exitCode, _, stderr) = RunAndCaptureStreams(["--integrity-check", "--db", missingDb]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Contains("database not found", stderr);
        Assert.Contains("cdidx index <projectPath>", stderr);
    }

    [Fact]
    public void Run_IntegrityCheck_FileUriSemicolonPayloadDoesNotCreateDatabase_Issue3220()
    {
        var missingDb = TestProjectHelper.CreateTempDbPath("cdidx_db_uri_injection");
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
            DeleteDbFile(missingDb);
        }
    }

    [Fact]
    public void Run_IntegrityCheck_FileUriJsonReportsUriWithoutPathNormalization_Issue3221()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_uri_display");
        try
        {
            InitializeEmptyDb(dbPath);

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", dbUri, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(dbUri, json.GetProperty("db_path").GetString());
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_FileUriHumanOutputReportsUriWithoutPathNormalization_Issue3221()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_uri_schema_display");
        try
        {
            InitializeEmptyDb(dbPath);

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (exitCode, stdout, _) = RunAndCaptureStreams(["schema", "--db", dbUri]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains($"database    : {dbUri}", stdout);
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_MissingDb_JsonShapeIncludesHint()
    {
        var missingDb = TestProjectHelper.CreateTempDbPath("cdidx_db_missing");

        var (exitCode, json) = RunAndCaptureJson(["--integrity-check", "--db", missingDb, "--json"]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("database not found", json.GetProperty("message").GetString());
        Assert.Contains("cdidx index <projectPath>", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void Run_CleanDb_ReturnsOk()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_clean");
        try
        {
            InitializeEmptyDb(dbPath);

            var (exitCode, stdout, _) = RunAndCaptureStreams(["--integrity-check", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Integrity check", stdout);
            Assert.Contains("ok", stdout);
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_CleanDb_JsonReportsOkTrueAndEmptyIssues()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_clean_json");
        try
        {
            InitializeEmptyDb(dbPath);

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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_IntegritySubcommand_JsonReportsOk_Issue3958()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_integrity_alias");
        try
        {
            InitializeEmptyDb(dbPath);

            var (exitCode, json) = RunAndCaptureJson(["integrity", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal("integrity_ok", json.GetProperty("diagnostic_code").GetString());
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_IntegrityCheck_JsonCancellationReturnsInterrupted_Issue3811()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_integrity_cancel");
        try
        {
            InitializeEmptyDb(dbPath);
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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_IntegrityCheck_JsonReportsStableErrorSeverity()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_integrity_error_json");
        DbCommandRunner.IntegrityCheckRowsForTesting = () => ["simulated corruption"];
        try
        {
            InitializeEmptyDb(dbPath);

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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_JsonIncludesTablesAndUserVersion()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_schema");
        try
        {
            InitializeEmptyDb(dbPath);

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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_JsonFiltersByTypeAndName_Issue3958()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_schema_filter");
        try
        {
            InitializeEmptyDb(dbPath);

            var (exitCode, json) = RunAndCaptureJson(["schema", "--db", dbPath, "--json", "--type", "table", "--name", "files"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("table", json.GetProperty("type_filter").GetString());
            Assert.Equal("files", json.GetProperty("name_filter").GetString());
            Assert.False(json.GetProperty("summary_only").GetBoolean());
            Assert.Equal(1, json.GetProperty("object_type_counts").GetProperty("table").GetInt32());
            Assert.Equal(0, json.GetProperty("object_type_omitted_counts").GetProperty("table").GetInt32());
            var entry = Assert.Single(json.GetProperty("entries").EnumerateArray());
            Assert.Equal("table", entry.GetProperty("type").GetString());
            Assert.Equal("files", entry.GetProperty("name").GetString());
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_SummaryOnlyOmitsEntriesButKeepsCounts_Issue3958()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_schema_summary");
        try
        {
            InitializeEmptyDb(dbPath);

            var (exitCode, json) = RunAndCaptureJson(["schema", "--db", dbPath, "--json", "--type", "table", "--name", "files", "--summary-only"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.Equal("table", json.GetProperty("type_filter").GetString());
            Assert.Equal("files", json.GetProperty("name_filter").GetString());
            Assert.Equal(1, json.GetProperty("object_type_counts").GetProperty("table").GetInt32());
            Assert.Equal(1, json.GetProperty("object_type_omitted_counts").GetProperty("table").GetInt32());
            Assert.Equal(0, json.GetProperty("emitted_count").GetInt32());
            Assert.Equal(1, json.GetProperty("omitted_count").GetInt32());
            Assert.Equal(1, json.GetProperty("summary_only_omitted_count").GetInt32());
            Assert.Equal(0, json.GetProperty("entries").GetArrayLength());
            Assert.False(json.GetProperty("truncated").GetBoolean());
            Assert.False(json.TryGetProperty("row_limit_reached", out _));
            Assert.Equal("summary_only", Assert.Single(json.GetProperty("omitted_by").EnumerateArray().ToList()).GetString());
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_JsonReportsObjectTypeOmissionsWhenEntryLimitTruncates()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_schema_truncated");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                for (var i = 0; i < DbCommandRunner.SchemaEntryLimit + 5; i++)
                    Execute(connection, $"CREATE TABLE t_{i:D3}(id INTEGER PRIMARY KEY);");
            }
            ReleaseSqlitePools();

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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_Prune_DryRunCountsAndApplyDeletesOrphans()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_prune");
        try
        {
            InitializeDbWithOrphans(dbPath);

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
            using (var verify = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                verify.Open();
                using var userVersion = verify.CreateCommand();
                userVersion.CommandText = "PRAGMA user_version";
                var value = checked((int)(long)userVersion.ExecuteScalar()!);
                Assert.Equal(0, value & DbContext.HotspotReferenceAggregateReadyFlag);
                Assert.NotEqual(0, value & DbContext.HotspotReferenceAggregateStorageContractFlag);
            }

            var (secondExit, secondJson) = RunAndCaptureJson(["prune", "--dry-run", "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, secondExit);
            Assert.Equal(0, secondJson.GetProperty("total").GetInt32());
        }
        finally
        {
            DbContext.WalCheckpointTruncateExecutedForTesting = null;
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_PruneApply_JsonReportsWalCheckpointWarning_Issue3514()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_prune_wal_warning");
        try
        {
            InitializeDbWithOrphans(dbPath);
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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_PruneDryRun_ReportsMaintenanceProgress_Issue3811()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_prune_progress");
        var progress = new List<string>();
        try
        {
            InitializeDbWithOrphans(dbPath);
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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_CheckpointAndRestore_RestoresDatabaseBytes()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);

            var originalBytes = File.ReadAllBytes(dbPath);
            var (checkpointExit, checkpointOut, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            Assert.Contains("saved", checkpointOut);

            MutateValidDatabase(dbPath);

            var (restoreExit, restoreOut, _) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.Success, restoreExit);
            Assert.Contains("Restored", restoreOut);
            Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
            Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreDryRun_ValidatesManifestPathsAndSpaceWithoutMutation_Issue4717()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_dry_run_4717");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            MutateValidDatabase(dbPath);
            var changedBytes = File.ReadAllBytes(dbPath);
            DbCommandRunner.AvailableFreeSpaceForTesting = _ => 1_000_000;

            var (exitCode, json) = RunAndCaptureJson(["restore", "saved", "--dry-run", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("dry_run").GetBoolean());
            Assert.True(json.GetProperty("ready").GetBoolean());
            Assert.True(json.GetProperty("manifest_valid").GetBoolean());
            Assert.True(json.GetProperty("paths_valid").GetBoolean());
            Assert.True(json.GetProperty("space_check_available").GetBoolean());
            Assert.True(json.GetProperty("space_sufficient").GetBoolean());
            Assert.True(json.GetProperty("required_space_bytes").GetInt64() > 0);
            Assert.Equal(1_000_000, json.GetProperty("available_space_bytes").GetInt64());
            Assert.Contains(
                json.GetProperty("files").EnumerateArray(),
                file => file.GetString() == "codeindex.db");
            Assert.Equal(changedBytes, File.ReadAllBytes(dbPath));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-tmp-*"));
        }
        finally
        {
            DbCommandRunner.AvailableFreeSpaceForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreDryRun_ResolvesLinkedDestinationBeforeSpaceProbe_Issue4717()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_linked_space_4717");
        var targetDirectory = Path.Combine(root, "target");
        var linkedDirectory = Path.Combine(root, "linked");
        var dbPath = Path.Combine(linkedDirectory, "codeindex.db");
        string? probedDirectory = null;
        try
        {
            Directory.CreateDirectory(targetDirectory);
            Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);
            InitializeEmptyDb(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            DbCommandRunner.AvailableFreeSpaceForTesting = path =>
            {
                probedDirectory = path;
                return 1_000_000;
            };

            var (exitCode, json) = RunAndCaptureJson([
                "restore", "saved", "--dry-run", "--db", dbPath, "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(json.GetProperty("ready").GetBoolean());
            Assert.Equal(Path.GetFullPath(targetDirectory), probedDirectory);
            Assert.NotEqual(Path.GetFullPath(linkedDirectory), probedDirectory);
        }
        finally
        {
            DbCommandRunner.AvailableFreeSpaceForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreDryRun_InvalidManifestAndInsufficientSpaceReportBlockingDiagnostics_Issue4717()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_invalid_dry_run_4717");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            var (checkpointExit, checkpointJson) = RunAndCaptureJson(["checkpoint", "saved", "--db", dbPath, "--json"]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            var checkpointPath = checkpointJson.GetProperty("checkpoint_path").GetString()!;
            File.WriteAllText(
                Path.Combine(checkpointPath, "manifest.txt"),
                $"name=other{Environment.NewLine}created_at_utc=2026-01-01T00:00:00.0000000+00:00{Environment.NewLine}db_file=codeindex.db{Environment.NewLine}");
            var before = File.ReadAllBytes(dbPath);
            DbCommandRunner.AvailableFreeSpaceForTesting = _ => 0;

            var (exitCode, json) = RunAndCaptureJson(["restore", "saved", "--dry-run", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal("invalid", json.GetProperty("status").GetString());
            Assert.False(json.GetProperty("ready").GetBoolean());
            Assert.False(json.GetProperty("manifest_valid").GetBoolean());
            Assert.True(json.GetProperty("paths_valid").GetBoolean());
            Assert.False(json.GetProperty("space_sufficient").GetBoolean());
            Assert.Equal(CommandErrorCodes.DbError, json.GetProperty("error_code").GetString());
            var diagnostics = json.GetProperty("diagnostics").EnumerateArray().ToArray();
            Assert.Contains(diagnostics, diagnostic => diagnostic.GetProperty("code").GetString() == "checkpoint_manifest_invalid");
            Assert.Contains(diagnostics, diagnostic => diagnostic.GetProperty("code").GetString() == "checkpoint_space_insufficient");
            Assert.Equal(before, File.ReadAllBytes(dbPath));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
        }
        finally
        {
            DbCommandRunner.AvailableFreeSpaceForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreMissingCheckpoint_JsonUsesCheckpointErrorCode_Issue4337()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_missing_checkpoint_4337");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);

            var (exitCode, json) = RunAndCaptureJson(["restore", "missing", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.NotFound, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("checkpoint not found", json.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal(CommandErrorCodes.CheckpointNotFound, json.GetProperty("error_code").GetString());
            Assert.Contains("checkpoints --list", json.GetProperty("hint").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointHyphenatedName_DryRunAndWriteUseSameName_Issue4337()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_hyphen_4337");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);

            var (dryRunExit, dryRunJson) = RunAndCaptureJson(["checkpoint", "round7-real", "--dry-run", "--db", dbPath, "--json"]);
            var (writeExit, writeJson) = RunAndCaptureJson(["checkpoint", "round7-real", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, dryRunExit);
            Assert.Equal(CommandExitCodes.Success, writeExit);
            Assert.Equal("round7-real", dryRunJson.GetProperty("name").GetString());
            Assert.Equal("round7-real", writeJson.GetProperty("name").GetString());
            Assert.True(Directory.Exists(writeJson.GetProperty("checkpoint_path").GetString()));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointDryRun_JsonPreviewsFilesWithoutCreatingCheckpoint_Issue3937()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_dry_run");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            File.WriteAllText(dbPath, "db");
            File.WriteAllText(dbPath + "-wal", "wal");
            File.WriteAllText(dbPath + "-shm", "shm!");

            var (exitCode, json) = RunAndCaptureJson(["checkpoint", "preview", "--dry-run", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("dry_run").GetBoolean());
            Assert.Equal(9, json.GetProperty("bytes").GetInt64());
            Assert.False(Directory.Exists(dbPath + ".checkpoints"));
            var files = json.GetProperty("files").EnumerateArray().Select(file => file.GetString()).ToArray();
            Assert.Contains("codeindex.db", files);
            Assert.Contains("codeindex.db-wal", files);
            Assert.Contains("codeindex.db-shm", files);
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointTraversalName_JsonUsesUsageErrorAndSyntaxHint_Issue4477()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_traversal_4477");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            File.WriteAllText(dbPath, "db");

            var (exitCode, json) = RunAndCaptureJson(["checkpoint", "../escape", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.Contains("invalid checkpoint name", json.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal(CommandErrorCodes.UsageError, json.GetProperty("error_code").GetString());
            Assert.Contains("non-blank single file name", json.GetProperty("hint").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("writable", json.GetProperty("hint").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(dbPath + ".checkpoints"));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointRejectsOversizedNameBeforePathConstruction_Issue3124()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_name_cap");
        var dbPath = Path.Combine(root, "codeindex.db");
        var name = new string('a', DbCommandRunner.MaxCheckpointNameLength + 1);
        try
        {
            File.WriteAllText(dbPath, "db");

            var (exitCode, _, stderr) = RunAndCaptureStreams(["checkpoint", name, "--db", dbPath]);

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Contains($"checkpoint name is too long ({name.Length} characters; max {DbCommandRunner.MaxCheckpointNameLength})", stderr);
            Assert.Contains("truncated; original length", stderr);
            Assert.DoesNotContain(name, stderr);
            Assert.False(Directory.Exists(dbPath + ".checkpoints"));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_Checkpoint_OnPosix_WritesPrivateSnapshotPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_private");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointManifestOmitsAbsoluteDbPath_Issue3833()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_manifest");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointJsonSuccessKeepsDiagnosticsArray_Issue3812()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_json_contract");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            File.WriteAllText(dbPath, "db");

            var (checkpointExit, json) = RunAndCaptureJson(["checkpoint", "contract", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            var diagnostics = json.GetProperty("diagnostics");
            Assert.Equal(JsonValueKind.Array, diagnostics.ValueKind);
            Assert.Equal(0, diagnostics.GetArrayLength());
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointInjectedClockControlsNameAndManifest_Issue3963()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_clock");
        var dbPath = Path.Combine(root, "codeindex.db");
        var fixedTime = new DateTimeOffset(2026, 6, 23, 4, 5, 6, 789, TimeSpan.Zero);
        try
        {
            File.WriteAllText(dbPath, "db");
            DbCommandRunner.UtcNowForTesting = () => fixedTime;

            var (checkpointExit, json) = RunAndCaptureJson(["checkpoint", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            var checkpointName = json.GetProperty("name").GetString();
            Assert.NotNull(checkpointName);
            Assert.StartsWith("20260623040506789-", checkpointName, StringComparison.Ordinal);
            Assert.Equal(50, checkpointName.Length);
            var checkpointPath = json.GetProperty("checkpoint_path").GetString();
            Assert.NotNull(checkpointPath);
            var manifest = File.ReadAllText(Path.Combine(checkpointPath, "manifest.txt"));
            Assert.Contains($"created_at_utc={fixedTime:O}", manifest, StringComparison.Ordinal);
        }
        finally
        {
            DbCommandRunner.UtcNowForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointAutomaticNameAvoidsInjectedClockCollision_Issue3987()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_collision");
        var dbPath = Path.Combine(root, "codeindex.db");
        var fixedTime = new DateTimeOffset(2026, 6, 23, 4, 5, 6, 789, TimeSpan.Zero);
        try
        {
            File.WriteAllText(dbPath, "db");
            DbCommandRunner.UtcNowForTesting = () => fixedTime;

            var (firstExit, firstJson) = RunAndCaptureJson(["checkpoint", "--db", dbPath, "--json"]);
            var (secondExit, secondJson) = RunAndCaptureJson(["checkpoint", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.Success, firstExit);
            Assert.Equal(CommandExitCodes.Success, secondExit);
            var firstName = firstJson.GetProperty("name").GetString();
            var secondName = secondJson.GetProperty("name").GetString();
            Assert.NotNull(firstName);
            Assert.NotNull(secondName);
            Assert.StartsWith("20260623040506789-", firstName, StringComparison.Ordinal);
            Assert.StartsWith("20260623040506789-", secondName, StringComparison.Ordinal);
            Assert.NotEqual(firstName, secondName);
            Assert.True(Directory.Exists(Path.Combine(dbPath + ".checkpoints", firstName)));
            Assert.True(Directory.Exists(Path.Combine(dbPath + ".checkpoints", secondName)));
        }
        finally
        {
            DbCommandRunner.UtcNowForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointJsonReportsRecoverableFileNameEnumerationFailure_Issue3833()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_enum");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_Checkpoint_JsonReportsFileEnumerationDiagnostic_Issue3812()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_file_enum");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointTempCleanupFailurePreservesOriginalFailure_Issue3029()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_cleanup");
        var dbPath = Path.Combine(root, "codeindex.db");
        string? cleanupPath = null;
        try
        {
            InitializeEmptyDb(dbPath);

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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void TryDeleteTemporaryDirectory_RejectsTargetOutsideSafeRoot_Issue3379()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_cleanup_safe_root");
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void TryDeleteTemporaryDirectory_RejectsReparseCleanupTarget_Issue3732()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_db_cleanup_reparse");
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointsList_JsonIncludesCreatedCheckpoint()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_list");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);

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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointsList_CapsCheckpointAndFileEnumeration_Issue2880()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_list_cap");
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
                if (i == 0)
                {
                    for (var file = 0; file < DbCommandRunner.CheckpointFileInspectLimit + 1; file++)
                        File.WriteAllText(Path.Combine(checkpointPath, $"extra-{file:D4}.txt"), "x");
                }
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointsPruneAndDelete_DryRunReportsExactPathsBeforeMutation_Issue4717()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_cleanup_4717");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            foreach (var name in new[] { "older", "middle", "newer" })
            {
                var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", name, "--db", dbPath]);
                Assert.Equal(CommandExitCodes.Success, checkpointExit);
            }

            var checkpointRoot = dbPath + ".checkpoints";
            var older = Path.Combine(checkpointRoot, "older");
            var middle = Path.Combine(checkpointRoot, "middle");
            var newer = Path.Combine(checkpointRoot, "newer");
            Directory.SetCreationTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetCreationTimeUtc(middle, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetCreationTimeUtc(newer, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

            var (dryRunExit, dryRunJson) = RunAndCaptureJson([
                "checkpoints", "--prune", "--keep", "1", "--dry-run", "--db", dbPath, "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, dryRunExit);
            Assert.Equal("dry_run", dryRunJson.GetProperty("status").GetString());
            Assert.True(dryRunJson.GetProperty("dry_run").GetBoolean());
            Assert.Equal(
                new[] { middle, older },
                dryRunJson.GetProperty("deleted_paths").EnumerateArray().Select(path => path.GetString()).ToArray());
            Assert.Equal(
                new[] { newer },
                dryRunJson.GetProperty("retained_paths").EnumerateArray().Select(path => path.GetString()).ToArray());
            Assert.All(new[] { older, middle, newer }, path => Assert.True(Directory.Exists(path)));

            var (pruneExit, pruneJson) = RunAndCaptureJson([
                "checkpoints", "--prune", "--keep", "1", "--db", dbPath, "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, pruneExit);
            Assert.Equal(2, pruneJson.GetProperty("deleted").GetInt32());
            Assert.False(Directory.Exists(older));
            Assert.False(Directory.Exists(middle));
            Assert.True(Directory.Exists(newer));

            var (deletePreviewExit, deletePreviewJson) = RunAndCaptureJson([
                "checkpoints", "--delete", "newer", "--dry-run", "--db", dbPath, "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, deletePreviewExit);
            Assert.Equal(newer, Assert.Single(deletePreviewJson.GetProperty("deleted_paths").EnumerateArray()).GetString());
            Assert.True(Directory.Exists(newer));

            var (deleteExit, deleteJson) = RunAndCaptureJson([
                "checkpoints", "--delete", "newer", "--db", dbPath, "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, deleteExit);
            Assert.Equal(1, deleteJson.GetProperty("deleted").GetInt32());
            Assert.False(Directory.Exists(newer));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointsPrune_UsesManifestTimestampWhenDirectoryCreationTimesTie_Issue4717()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_manifest_retention_4717");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            DbCommandRunner.UtcNowForTesting = () => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var (olderExit, _, _) = RunAndCaptureStreams(["checkpoint", "a-old", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, olderExit);

            DbCommandRunner.UtcNowForTesting = () => new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
            var (newerExit, _, _) = RunAndCaptureStreams(["checkpoint", "z-new", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, newerExit);

            var checkpointRoot = dbPath + ".checkpoints";
            var older = Path.Combine(checkpointRoot, "a-old");
            var newer = Path.Combine(checkpointRoot, "z-new");
            var tiedCreationTime = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
            Directory.SetCreationTimeUtc(older, tiedCreationTime);
            Directory.SetCreationTimeUtc(newer, tiedCreationTime);

            var (dryRunExit, json) = RunAndCaptureJson([
                "checkpoints", "--prune", "--keep", "1", "--dry-run", "--db", dbPath, "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, dryRunExit);
            Assert.Equal(older, Assert.Single(json.GetProperty("deleted_paths").EnumerateArray()).GetString());
            Assert.Equal(newer, Assert.Single(json.GetProperty("retained_paths").EnumerateArray()).GetString());
            Assert.True(Directory.Exists(older));
            Assert.True(Directory.Exists(newer));
        }
        finally
        {
            DbCommandRunner.UtcNowForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointsPrune_DoesNotLetMalformedCheckpointConsumeRetentionSlot_Issue4717()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_invalid_retention_4717");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            foreach (var name in new[] { "valid", "malformed" })
            {
                var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", name, "--db", dbPath]);
                Assert.Equal(CommandExitCodes.Success, checkpointExit);
            }

            var checkpointRoot = dbPath + ".checkpoints";
            var valid = Path.Combine(checkpointRoot, "valid");
            var malformed = Path.Combine(checkpointRoot, "malformed");
            Directory.SetCreationTimeUtc(valid, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetCreationTimeUtc(malformed, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(Path.Combine(malformed, "manifest.txt"), "not-a-manifest");

            var (dryRunExit, dryRunJson) = RunAndCaptureJson([
                "checkpoints", "--prune", "--keep", "1", "--dry-run", "--db", dbPath, "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, dryRunExit);
            Assert.Equal(malformed, Assert.Single(dryRunJson.GetProperty("deleted_paths").EnumerateArray()).GetString());
            Assert.Equal(valid, Assert.Single(dryRunJson.GetProperty("retained_paths").EnumerateArray()).GetString());
            Assert.Contains(
                dryRunJson.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString() == "checkpoint_retention_invalid");
            Assert.True(Directory.Exists(valid));
            Assert.True(Directory.Exists(malformed));

            var (pruneExit, pruneJson) = RunAndCaptureJson([
                "checkpoints", "--prune", "--keep", "1", "--db", dbPath, "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, pruneExit);
            Assert.Equal(malformed, Assert.Single(pruneJson.GetProperty("deleted_paths").EnumerateArray()).GetString());
            Assert.True(Directory.Exists(valid));
            Assert.False(Directory.Exists(malformed));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_CheckpointsDelete_RejectsSymlinkedCheckpointRoot_Issue4717()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_root_link_4717");
        var externalRoot = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_external_4717");
        var dbPath = Path.Combine(root, "codeindex.db");
        var checkpointRoot = dbPath + ".checkpoints";
        var externalCheckpoint = Path.Combine(externalRoot, "victim");
        try
        {
            File.WriteAllText(dbPath, "current");
            Directory.CreateDirectory(externalCheckpoint);
            File.WriteAllText(Path.Combine(externalCheckpoint, "codeindex.db"), "must remain");
            Directory.CreateSymbolicLink(checkpointRoot, externalRoot);

            var (exitCode, json) = RunAndCaptureJson([
                "checkpoints", "--delete", "victim", "--db", dbPath, "--json",
            ]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Equal("error", json.GetProperty("status").GetString());
            Assert.True(Directory.Exists(externalCheckpoint));
            Assert.Equal("must remain", File.ReadAllText(Path.Combine(externalCheckpoint, "codeindex.db")));
            Assert.Contains(
                json.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString() == "checkpoint_delete_skipped");
        }
        finally
        {
            File.Delete(checkpointRoot);
            DeleteWorkDirectory(root);
            DeleteWorkDirectory(externalRoot);
        }
    }

    [Fact]
    public void Run_RestoreIncompleteCheckpoint_ReturnsErrorAndKeepsDatabase()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_bad");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);

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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreFailureAfterBackup_RestoresOriginalDatabase()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_fail");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);

            var originalBytes = File.ReadAllBytes(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            MutateValidDatabase(dbPath);
            var changedBytes = File.ReadAllBytes(dbPath);
            DbCommandRunner.RestoreFailureAfterBackupForTesting = () => throw new IOException("injected restore failure");

            var (restoreExit, _, stderr) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Contains("IOException", stderr);
            Assert.DoesNotContain("injected restore failure", stderr);
            Assert.Equal(changedBytes, File.ReadAllBytes(dbPath));
            Assert.Single(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-tmp-*"));
        }
        finally
        {
            DbCommandRunner.RestoreFailureAfterBackupForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreCancellationAfterBackup_RollsBackAndReturnsInterrupted_Issue4857()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_cancel");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            MutateValidDatabase(dbPath);
            var changedBytes = File.ReadAllBytes(dbPath);
            DbCommandRunner.RestoreFailureAfterBackupForTesting = () =>
                throw new OperationCanceledException("injected restore cancellation");

            var (restoreExit, stdout, stderr) = RunAndCaptureStreams(
                ["restore", "saved", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.CancelledBySignal, restoreExit);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(
                CommandErrorCodes.Interrupted,
                document.RootElement.GetProperty("error_code").GetString());
            Assert.Equal(changedBytes, File.ReadAllBytes(dbPath));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-tmp-*"));
        }
        finally
        {
            DbCommandRunner.RestoreFailureAfterBackupForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreNoBackupRollbackFailureRetainsTransientOriginal_Issue4857()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_no_backup_rollback_fail");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            MutateValidDatabase(dbPath);
            var changedBytes = File.ReadAllBytes(dbPath);
            DbCommandRunner.RestoreFailureAfterBackupForTesting = () =>
            {
                Directory.CreateDirectory(dbPath);
                throw new IOException("injected no-backup restore failure");
            };

            var (restoreExit, stdout, _) = RunAndCaptureStreams(
                ["restore", "saved", "--no-backup", "--db", dbPath, "--json"]);

            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            using var document = JsonDocument.Parse(stdout);
            Assert.True(document.RootElement.GetProperty("rollback_failed").GetBoolean());
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
            var restoreTempPath = Assert.Single(
                Directory.GetDirectories(root, "codeindex.db.restore-tmp-*"));
            var retainedOriginalPath = Path.Combine(restoreTempPath, "rollback", "codeindex.db");
            Assert.True(File.Exists(retainedOriginalPath));
            Assert.Equal(changedBytes, File.ReadAllBytes(retainedOriginalPath));
        }
        finally
        {
            DbCommandRunner.RestoreFailureAfterBackupForTesting = null;
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreRollbackFailurePreservesPrimaryFailure_Issue3514()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_rollback_fail");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);

            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            MutateValidDatabase(dbPath);
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreRollbackFailureJsonIncludesStructuredMetadata_Issue3833()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_rollback_json");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);

            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            MutateValidDatabase(dbPath);
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreBackupsListAndPruneOrdersByRecency_Issue3833()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_backups");
        var dbPath = Path.Combine(root, "codeindex.db");
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
            Assert.Equal(older, Assert.Single(pruneJson.GetProperty("deleted_paths").EnumerateArray()).GetString());
            Assert.Equal(newer, Assert.Single(pruneJson.GetProperty("retained_paths").EnumerateArray()).GetString());
            Assert.False(Directory.Exists(older));
            Assert.True(Directory.Exists(newer));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreBackupsPruneDryRunReportsExactPathsWithoutDeleting_Issue4717()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_backups_dry_run");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            File.WriteAllText(dbPath, "current");
            var backup = Path.Combine(root, "codeindex.db.restore-backup-20260101000000000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "codeindex.db"), "backup");

            var (exitCode, json) = RunAndCaptureJson([
                "restore-backups", "--prune", "--dry-run", "--keep", "0", "--db", dbPath, "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal("dry_run", json.GetProperty("status").GetString());
            Assert.True(json.GetProperty("dry_run").GetBoolean());
            Assert.Equal(1, json.GetProperty("deleted").GetInt32());
            Assert.Equal(0, json.GetProperty("retained").GetInt32());
            Assert.Equal(backup, Assert.Single(json.GetProperty("deleted_paths").EnumerateArray()).GetString());
            Assert.Empty(json.GetProperty("retained_paths").EnumerateArray());
            Assert.True(Directory.Exists(backup));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreBackupsPruneSkipsDeletionWhenScanTruncated_Issue3833()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_backups_truncated");
        var dbPath = Path.Combine(root, "codeindex.db");
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreBackupsPruneDeletesWhenOnlyFileInspectionTruncated_Issue3833()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_backups_file_truncated");
        var dbPath = Path.Combine(root, "codeindex.db");
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreRejectsSymlinkedCheckpointPayload_Issue3514()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_symlink");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            var originalBytes = File.ReadAllBytes(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            var checkpointDbPath = Path.Combine(dbPath + ".checkpoints", "saved", "codeindex.db");
            File.Delete(checkpointDbPath);
            var targetPath = Path.Combine(root, "payload-target.db");
            File.WriteAllText(targetPath, "not the checkpoint");
            File.CreateSymbolicLink(checkpointDbPath, targetPath);

            var (previewExit, previewJson) = RunAndCaptureJson(["restore", "saved", "--dry-run", "--db", dbPath, "--json"]);
            var (restoreExit, _, stderr) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, previewExit);
            Assert.False(previewJson.GetProperty("ready").GetBoolean());
            Assert.False(previewJson.GetProperty("paths_valid").GetBoolean());
            Assert.Contains(
                previewJson.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString() == "checkpoint_payload_invalid");
            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Contains("InvalidOperationException", stderr);
            Assert.DoesNotContain("not a regular file", stderr);
            Assert.DoesNotContain(checkpointDbPath, stderr);
            Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreRejectsSymlinkedCheckpointSidecar_Issue3812()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_sidecar_symlink");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreDryRunRejectsDirectoryCheckpointSidecar_Issue4717()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_checkpoint_sidecar_directory_4717");
        var dbPath = Path.Combine(root, "codeindex.db");
        try
        {
            InitializeEmptyDb(dbPath);
            var originalBytes = File.ReadAllBytes(dbPath);
            var (checkpointExit, checkpointJson) = RunAndCaptureJson([
                "checkpoint", "saved", "--db", dbPath, "--json",
            ]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);
            var checkpointPath = checkpointJson.GetProperty("checkpoint_path").GetString()!;
            Directory.CreateDirectory(Path.Combine(checkpointPath, "codeindex.db-wal"));

            var (previewExit, previewJson) = RunAndCaptureJson([
                "restore", "saved", "--dry-run", "--db", dbPath, "--json",
            ]);
            var (restoreExit, _, _) = RunAndCaptureStreams(["restore", "saved", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, previewExit);
            Assert.False(previewJson.GetProperty("ready").GetBoolean());
            Assert.False(previewJson.GetProperty("paths_valid").GetBoolean());
            Assert.Contains(
                previewJson.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("code").GetString() == "checkpoint_payload_invalid");
            Assert.Equal(CommandExitCodes.DatabaseError, restoreExit);
            Assert.Equal(originalBytes, File.ReadAllBytes(dbPath));
            Assert.Empty(Directory.GetDirectories(root, "codeindex.db.restore-backup-*"));
        }
        finally
        {
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreTemporaryNamesIncludeCollisionResistantSuffix_Issue3031()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_suffix");
        var dbPath = Path.Combine(root, "codeindex.db");
        var inspected = false;
        try
        {
            InitializeEmptyDb(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            MutateValidDatabase(dbPath);
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_RestoreTempCleanupFailureWarnsWithoutFailing_Issue3030()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_cleanup");
        var dbPath = Path.Combine(root, "codeindex.db");
        string? cleanupPath = null;
        try
        {
            InitializeEmptyDb(dbPath);
            var originalBytes = File.ReadAllBytes(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            MutateValidDatabase(dbPath);
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
            DeleteWorkDirectory(root);
        }
    }

    [Fact]
    public void Run_Restore_OnPosix_CreatesPrivateStagingAndBackupPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;

        var root = TestProjectHelper.CreateTempProject("cdidx_db_restore_private");
        var dbPath = Path.Combine(root, "codeindex.db");
        var inspected = false;
        try
        {
            InitializeEmptyDb(dbPath);
            var (checkpointExit, _, _) = RunAndCaptureStreams(["checkpoint", "saved", "--db", dbPath]);
            Assert.Equal(CommandExitCodes.Success, checkpointExit);

            MutateValidDatabase(dbPath);
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
            DeleteWorkDirectory(root);
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
            DeleteDbFile(dbPath);
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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_JsonCapsEntriesAndSqlText_Issue2881()
    {
        var dbPath = TestProjectHelper.CreateTempDbPath("cdidx_db_schema_cap");
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
            ReleaseSqlitePools();

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
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Run_Schema_JsonHonorsSizeControlsAndInternalFilter_Issue3937()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_db_schema_controls_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ConnectionString))
            {
                connection.Open();
                Execute(connection, "CREATE TABLE visible_table(id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT, extra TEXT)");
                Execute(connection, "CREATE TABLE z_second(id INTEGER PRIMARY KEY, value TEXT)");
            }
            ReleaseSqlitePools();

            var (exitCode, json) = RunAndCaptureJson(["schema", "--db", dbPath, "--json", "--type", "table", "--limit", "1", "--max-sql-chars", "12", "--exclude-internal"]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.False(json.GetProperty("include_internal").GetBoolean());
            Assert.Equal(1, json.GetProperty("entry_limit").GetInt32());
            Assert.Equal(12, json.GetProperty("sql_text_limit").GetInt32());
            Assert.True(json.GetProperty("entries_truncated").GetBoolean());
            Assert.True(json.GetProperty("sql_truncated").GetBoolean());
            Assert.Equal(2, json.GetProperty("object_type_counts").GetProperty("table").GetInt32());
            Assert.Equal(1, json.GetProperty("object_type_omitted_counts").GetProperty("table").GetInt32());
            var entry = Assert.Single(json.GetProperty("entries").EnumerateArray());
            Assert.NotEqual("sqlite_sequence", entry.GetProperty("name").GetString());
            Assert.EndsWith(" [truncated]", entry.GetProperty("sql").GetString());
        }
        finally
        {
            DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void DbHelp_IncludesSafetyNotesForMaintenanceSubcommands_Issue3937()
    {
        var (_, stdout, _) = ConsoleCapture.Capture(() =>
        {
            ConsoleUi.PrintCommandUsage("db");
            return 0;
        });

        Assert.Contains("checkpoint --dry-run", stdout);
        Assert.Contains("schema defaults to the full sqlite_master dump", stdout);
        Assert.Contains("manifest, regular-file paths, rollback-backup policy, and destination free space", stdout);
        Assert.Contains("--delete and --prune remove snapshots", stdout);
        Assert.Contains("--prune --dry-run reports exact deleted/retained paths", stdout);
        Assert.Contains("prune --dry-run only counts", stdout);
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

    private static void MutateValidDatabase(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ConnectionString);
        connection.Open();
        Execute(connection, "CREATE TABLE restore_test_state (value TEXT NOT NULL)");
        Execute(connection, "INSERT INTO restore_test_state(value) VALUES ('changed')");
        ReleaseSqlitePools();
    }
}
