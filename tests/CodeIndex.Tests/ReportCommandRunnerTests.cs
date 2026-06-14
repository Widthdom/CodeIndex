using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for `cdidx report --output <path>` (issue #1552). The command must
/// produce a redacted `.tar.gz` containing version + OS + schema + a tail of
/// the lifecycle log, while never embedding indexed source content or unredacted
/// command-line arguments.
/// `cdidx report --output <path>` のテスト (issue #1552)。匿名化された
/// tarball にバージョン・OS・スキーマ・ログ末尾のみが入り、ソース内容や
/// 生の引数が混入しないことを担保する。
/// </summary>
[Collection("SQLite pool sensitive")]
public class ReportCommandRunnerTests
{
    private const UnixFileMode PermissionBits =
        UnixFileMode.UserRead |
        UnixFileMode.UserWrite |
        UnixFileMode.UserExecute |
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void ParseArgs_OutputFlagCapturesValue()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "bundle.tgz"]);

        Assert.Equal("bundle.tgz", options.OutputPath);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_ShortOutputAliasCapturesValue()
    {
        var options = ReportCommandRunner.ParseArgs(["-o", "bundle.tgz"]);

        Assert.Equal("bundle.tgz", options.OutputPath);
    }

    [Fact]
    public void ParseArgs_NoLogTurnsOffLogInclusion()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "x.tgz", "--no-log"]);

        Assert.False(options.IncludeLog);
    }

    [Fact]
    public void ParseArgs_IncludeArgsOptsInToLiteralLog()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "x.tgz", "--include-args"]);

        Assert.True(options.IncludeArgs);
    }

    [Fact]
    public void ParseArgs_LogLinesParsesPositive()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "x.tgz", "--log-lines", "50"]);

        Assert.Equal(50, options.LogLines);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_LogLinesAboveMaximumClamps_Issue2837()
    {
        var options = ReportCommandRunner.ParseArgs([
            "--output", "x.tgz",
            "--log-lines", (ReportCommandRunner.MaxLogLines + 1).ToString(),
        ]);

        Assert.Equal(ReportCommandRunner.MaxLogLines, options.LogLines);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_LogLinesNegativeReportsError()
    {
        var options = ReportCommandRunner.ParseArgs(["--output", "x.tgz", "--log-lines", "-3"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("non-negative", options.ParseError);
    }

    [Fact]
    public void ParseArgs_UnknownOptionRecordsParseError()
    {
        var options = ReportCommandRunner.ParseArgs(["--bogus"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("--bogus", options.ParseError);
    }

    [Fact]
    public void ParseArgs_PositionalArgRecordsParseError()
    {
        var options = ReportCommandRunner.ParseArgs(["extra"]);

        Assert.NotNull(options.ParseError);
        Assert.Contains("positional", options.ParseError);
    }

    [Fact]
    public void Run_MissingOutputFlag_ReturnsUsageError()
    {
        var (exitCode, _, stderr) = RunAndCaptureStreams([]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--output", stderr);
    }

    [Fact]
    public void Run_MissingOutputFlag_JsonShapeIncludesHint()
    {
        var (exitCode, json) = RunAndCaptureJson(["--json"]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Contains("--output", json.GetProperty("message").GetString());
        Assert.Contains("--output", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void Run_NoDbAndNoLog_StillProducesBundleWithMetadata()
    {
        var workDir = CreateWorkDir();
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var missingDb = Path.Combine(workDir, "missing.db");

            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", missingDb,
                "--no-log",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.True(File.Exists(output));

            var entries = ReadTarGzEntries(output);
            Assert.Contains("metadata.json", entries.Keys);
            Assert.Contains("version.txt", entries.Keys);
            Assert.Contains("env.txt", entries.Keys);
            Assert.Contains("schema.txt", entries.Keys);
            Assert.Contains("support-manifest.json", entries.Keys);
            Assert.Contains("README.md", entries.Keys);
            Assert.DoesNotContain("log/stderr-recent.log", entries.Keys);

            var schemaText = Encoding.UTF8.GetString(entries["schema.txt"]);
            Assert.Contains("no SQLite index found", schemaText);
            Assert.Contains($"no SQLite index found at: {ReportCommandRunner.RedactedPlaceholder}", schemaText);
            Assert.DoesNotContain(missingDb, schemaText);

            using var manifest = ReadJsonEntry(entries, "support-manifest.json");
            var root = manifest.RootElement;
            Assert.Equal(1, root.GetProperty("manifest_version").GetInt32());
            Assert.Equal(entries.Count, root.GetProperty("bundle").GetProperty("files").GetInt32());
            Assert.False(root.GetProperty("bundle").GetProperty("db_included").GetBoolean());
            Assert.False(root.GetProperty("bundle").GetProperty("log_included").GetBoolean());
            Assert.Equal(0, root.GetProperty("redactions").GetProperty("total").GetInt32());
            Assert.Equal("unavailable", root.GetProperty("readiness").GetProperty("source").GetString());
            Assert.Equal("database_unavailable", root.GetProperty("readiness").GetProperty("unavailable_reason").GetString());
            Assert.True(JsonArrayContains(root.GetProperty("omissions").GetProperty("schema"), "schema_unavailable_no_database"));
            Assert.True(JsonArrayContains(root.GetProperty("omissions").GetProperty("log"), "lifecycle_log_skipped_by_option"));
            Assert.DoesNotContain(missingDb, root.GetRawText());
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_OutputArchiveAndEntriesUseOwnerOnlyPermissions()
    {
        var workDir = CreateWorkDir();
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");

            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--no-log",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            if (!OperatingSystem.IsWindows())
            {
                var fileMode = File.GetUnixFileMode(output) & PermissionBits;
                Assert.Equal(ReportCommandRunner.BundleFileMode, fileMode);
            }

            var entryModes = ReadTarGzEntryModes(output);
            Assert.NotEmpty(entryModes);
            Assert.All(
                entryModes.Values,
                mode => Assert.Equal(ReportCommandRunner.BundleFileMode, mode & PermissionBits));
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void WriteBundle_FailurePreservesExistingBundle()
    {
        var workDir = CreateWorkDir();
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            File.WriteAllText(output, "existing bundle");
            var bundle = new ReportBundle();
            bundle.AddText("metadata.txt", "partial");

            Assert.Throws<IOException>(() =>
                ReportCommandRunner.WriteBundle(
                    output,
                    bundle,
                    beforeWriteEntries: () => throw new IOException("simulated report failure")));

            Assert.Equal("existing bundle", File.ReadAllText(output));
            Assert.Single(Directory.GetFiles(workDir));
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void WriteBundle_RelativeOutputUsesInitialFullPathWhenCurrentDirectoryChanges_Issue3147()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var workDir = CreateWorkDir();
        var driftDir = CreateWorkDir();
        try
        {
            Directory.SetCurrentDirectory(workDir);
            var bundle = new ReportBundle();
            bundle.AddText("metadata.txt", "ok");

            ReportCommandRunner.WriteBundle(
                "bundle.tgz",
                bundle,
                beforeWriteEntries: () => Directory.SetCurrentDirectory(driftDir));

            Assert.True(File.Exists(Path.Combine(workDir, "bundle.tgz")));
            Assert.False(File.Exists(Path.Combine(driftDir, "bundle.tgz")));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(driftDir);
        }
    }

    [Fact]
    public void Run_WithRealDb_SchemaTxtListsTablesAndRowCounts()
    {
        var workDir = CreateWorkDir();
        var dbPath = Path.Combine(workDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", dbPath,
                "--no-log",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            var schemaText = Encoding.UTF8.GetString(entries["schema.txt"]);
            Assert.Contains("tables", schemaText);
            Assert.Contains("files", schemaText);
            Assert.Contains("symbols", schemaText);
            Assert.Contains("row_count", schemaText);
            Assert.Contains($"database: {ReportCommandRunner.RedactedPlaceholder}", schemaText);
            Assert.DoesNotContain(dbPath, schemaText);
            Assert.DoesNotContain("no SQLite index found", schemaText);

            using var manifest = ReadJsonEntry(entries, "support-manifest.json");
            var readiness = manifest.RootElement.GetProperty("readiness");
            Assert.Equal("database", readiness.GetProperty("source").GetString());
            Assert.True(readiness.GetProperty("graph_table_available").GetBoolean());
            Assert.True(readiness.GetProperty("issues_table_available").GetBoolean());
            Assert.False(readiness.GetProperty("fold_ready").GetBoolean());
            Assert.True(JsonArrayContains(readiness.GetProperty("degraded_fields"), "fold_ready"));
            Assert.False(readiness.GetProperty("migration_in_progress").GetBoolean());
            Assert.DoesNotContain(dbPath, manifest.RootElement.GetRawText());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void BuildSchemaSummary_FileUriReadsExistingDatabase_Issue3148()
    {
        var workDir = CreateWorkDir();
        var dbPath = Path.Combine(workDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";
            var (schemaText, tables, reportedDbPath, dbIncluded, tablesTruncated) = ReportCommandRunner.BuildSchemaSummary(dbUri);

            Assert.True(dbIncluded);
            Assert.False(tablesTruncated);
            Assert.Equal(Path.GetFullPath(dbPath), reportedDbPath);
            Assert.Contains(tables, table => table.Name == "files");
            Assert.Contains("files", schemaText);
            Assert.DoesNotContain("no SQLite index found", schemaText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void BuildSchemaSummary_CapsTableEntries_Issue3146()
    {
        var workDir = CreateWorkDir();
        var dbPath = Path.Combine(workDir, "many-tables.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                for (var i = 0; i < ReportCommandRunner.MaxSchemaTables + 3; i++)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"CREATE TABLE {QuoteIdentifier($"table_{i:D3}")}(value INTEGER)";
                    cmd.ExecuteNonQuery();
                }
            }

            var (schemaText, tables, _, dbIncluded, tablesTruncated) = ReportCommandRunner.BuildSchemaSummary(dbPath);

            Assert.True(dbIncluded);
            Assert.True(tablesTruncated);
            Assert.Equal(ReportCommandRunner.MaxSchemaTables, tables.Count);
            Assert.Contains($"tables  : {ReportCommandRunner.MaxSchemaTables} (capped; additional tables omitted)", schemaText);
            Assert.Contains($"limits  : table entries <= {ReportCommandRunner.MaxSchemaTables}", schemaText);
            Assert.Contains("table_063 | 0", schemaText);
            Assert.DoesNotContain("table_064 |", schemaText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void BuildSchemaSummary_CapsDisplayedTableNamesAndRowCountScans_Issue3146()
    {
        var workDir = CreateWorkDir();
        var dbPath = Path.Combine(workDir, "large-table.db");
        var longName = "table_" + new string('a', ReportCommandRunner.MaxSchemaTableNameDisplayChars + 20);
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using (var createCmd = connection.CreateCommand())
                {
                    createCmd.CommandText = $"CREATE TABLE {QuoteIdentifier(longName)}(value INTEGER)";
                    createCmd.ExecuteNonQuery();
                }

                using var transaction = connection.BeginTransaction();
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = $"INSERT INTO {QuoteIdentifier(longName)}(value) VALUES ($value)";
                var valueParameter = insertCmd.CreateParameter();
                valueParameter.ParameterName = "$value";
                insertCmd.Parameters.Add(valueParameter);

                for (var i = 0; i < ReportCommandRunner.MaxSchemaRowCountScanRows + 5; i++)
                {
                    valueParameter.Value = i;
                    insertCmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            var (schemaText, tables, _, dbIncluded, tablesTruncated) = ReportCommandRunner.BuildSchemaSummary(dbPath);
            var table = Assert.Single(tables);

            Assert.True(dbIncluded);
            Assert.False(tablesTruncated);
            Assert.Equal(ReportCommandRunner.MaxSchemaRowCountScanRows, table.RowCount);
            Assert.True(table.RowCountTruncated);
            Assert.True(table.Name.Length <= ReportCommandRunner.MaxSchemaTableNameDisplayChars);
            Assert.Contains("[truncated]", table.Name);
            Assert.Contains($">={ReportCommandRunner.MaxSchemaRowCountScanRows}", schemaText);
            Assert.DoesNotContain(longName, schemaText);
            Assert.DoesNotContain((ReportCommandRunner.MaxSchemaRowCountScanRows + 5).ToString(), schemaText);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_WithLogDirOverride_IncludesRedactedTail()
    {
        var workDir = CreateWorkDir();
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "stderr-20260516.log"),
            string.Join('\n',
                "2026-05-16T03:00:00Z [INFO] session_start pid=1 version=1.21.0",
                "2026-05-16T03:00:00Z [INFO] process_path=/Users/widthdom/.dotnet/tools/cdidx",
                "2026-05-16T03:00:00Z [INFO] base_dir=/Users/widthdom/.dotnet/tools/.store/cdidx",
                "2026-05-16T03:00:00Z [INFO] cwd=/Users/widthdom/secret",
                "2026-05-16T03:00:00Z [ERROR] database_open_failed db=/Users/widthdom/secret/.cdidx/codeindex.db",
                "2026-05-16T03:00:00Z [INFO] config_file_loaded path=/Users/widthdom/secret/.cdidx/config.json",
                "2026-05-16T03:00:00Z [INFO] args=query \"SELECT * FROM secret\"",
                "2026-05-16T03:00:01Z [ERROR] sample error",
                ""));

        var previousLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--log-lines", "20",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            Assert.True(entries.ContainsKey("log/stderr-recent.log"));
            var logText = Encoding.UTF8.GetString(entries["log/stderr-recent.log"]);
            Assert.Contains($"# source directory: {ReportCommandRunner.RedactedPlaceholder}", logText);
            Assert.Contains("args=[redacted]", logText);
            Assert.Contains("cwd=[redacted]", logText);
            Assert.Contains("process_path=[redacted]", logText);
            Assert.Contains("base_dir=[redacted]", logText);
            Assert.Contains("db=[redacted]", logText);
            Assert.Contains("path=[redacted]", logText);
            Assert.DoesNotContain(logDir, logText);
            Assert.DoesNotContain("/Users/widthdom/secret", logText);
            Assert.DoesNotContain("/Users/widthdom/.dotnet/tools/cdidx", logText);
            Assert.DoesNotContain("/Users/widthdom/.dotnet/tools/.store/cdidx", logText);
            Assert.DoesNotContain("/Users/widthdom/secret/.cdidx/codeindex.db", logText);
            Assert.DoesNotContain("/Users/widthdom/secret/.cdidx/config.json", logText);
            Assert.DoesNotContain("SELECT * FROM secret", logText);
            Assert.Contains("session_start", logText);

            using var manifest = ReadJsonEntry(entries, "support-manifest.json");
            var root = manifest.RootElement;
            Assert.Equal(entries.Count, root.GetProperty("bundle").GetProperty("files").GetInt32());
            Assert.True(root.GetProperty("bundle").GetProperty("log_included").GetBoolean());
            Assert.Equal(8, root.GetProperty("bundle").GetProperty("log_lines_included").GetInt32());
            Assert.True(root.GetProperty("redactions").GetProperty("total").GetInt32() >= 6);
            var categories = root.GetProperty("redactions").GetProperty("categories");
            Assert.True(categories.GetProperty("args").GetInt32() >= 1);
            Assert.True(categories.GetProperty("path_fields").GetInt32() >= 5);
            Assert.DoesNotContain(logDir, root.GetRawText());
            Assert.DoesNotContain("/Users/widthdom/secret", root.GetRawText());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", previousLogDir);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_WithFullyIncludedShortLog_DoesNotReportTailOmission_Issue3555()
    {
        var workDir = CreateWorkDir();
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "stderr-20260519.log"),
            string.Join('\n',
                "2026-05-19T03:00:00Z [INFO] first",
                "2026-05-19T03:00:01Z [INFO] second",
                ""));

        var previousLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--log-lines", "20",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            using var manifest = ReadJsonEntry(entries, "support-manifest.json");
            var logOmissions = manifest.RootElement.GetProperty("omissions").GetProperty("log");

            Assert.Equal(2, manifest.RootElement.GetProperty("bundle").GetProperty("log_lines_included").GetInt32());
            Assert.False(JsonArrayContains(logOmissions, "older_log_lines_outside_tail_limit"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", previousLogDir);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_WithTruncatedLog_ReportsTailOmission_Issue3555()
    {
        var workDir = CreateWorkDir();
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "stderr-20260520.log"),
            string.Join('\n',
                "2026-05-20T03:00:00Z [INFO] omitted",
                "2026-05-20T03:00:01Z [INFO] kept-one",
                "2026-05-20T03:00:02Z [INFO] kept-two",
                ""));

        var previousLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--log-lines", "2",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            using var manifest = ReadJsonEntry(entries, "support-manifest.json");
            var logOmissions = manifest.RootElement.GetProperty("omissions").GetProperty("log");

            Assert.Equal(2, manifest.RootElement.GetProperty("bundle").GetProperty("log_lines_included").GetInt32());
            Assert.True(JsonArrayContains(logOmissions, "older_log_lines_outside_tail_limit"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", previousLogDir);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void BuildRecentLogTail_LargeLogReadsBoundedTail_Issue2837()
    {
        var workDir = CreateWorkDir();
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "stderr-20260517.log"),
            new string('x', ReportCommandRunner.MaxLogFileTailBytes + 512)
            + "\noldest-tail\nnewest-tail\n");

        var previousLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        try
        {
            var logText = ReportCommandRunner.BuildRecentLogTail(2, includeArgs: false, out var linesIncluded);

            Assert.Equal(2, linesIncluded);
            Assert.Contains("oldest-tail", logText);
            Assert.Contains("newest-tail", logText);
            Assert.DoesNotContain(new string('x', 128), logText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", previousLogDir);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void BuildRecentLogTail_ManyLogFilesKeepsNewestBoundedCandidates_Issue3026()
    {
        var workDir = CreateWorkDir();
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        var fileCount = ReportCommandRunner.MaxRecentLogFiles + 3;
        for (var i = 0; i < fileCount; i++)
        {
            File.WriteAllText(
                Path.Combine(logDir, $"stderr-20260518-{i:D4}.log"),
                $"line-{i:D4}\n");
        }

        var previousLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        try
        {
            var logText = ReportCommandRunner.BuildRecentLogTail(2, includeArgs: false, out var linesIncluded);

            var secondNewest = $"line-{fileCount - 2:D4}";
            var newest = $"line-{fileCount - 1:D4}";
            Assert.Equal(2, linesIncluded);
            Assert.Contains(secondNewest, logText);
            Assert.Contains(newest, logText);
            Assert.True(
                logText.IndexOf(secondNewest, StringComparison.Ordinal) <
                logText.IndexOf(newest, StringComparison.Ordinal));
            Assert.DoesNotContain("line-0000", logText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", previousLogDir);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_IncludeArgs_PreservesLiteralArgsButRedactsPaths()
    {
        var workDir = CreateWorkDir();
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        File.WriteAllText(
            Path.Combine(logDir, "stderr-20260516.log"),
            string.Join('\n',
                "2026-05-16T03:00:00Z [INFO] process_path=/tmp/cdidx",
                "2026-05-16T03:00:00Z [INFO] base_dir=/tmp/cdidx-store",
                "2026-05-16T03:00:00Z [INFO] cwd=/tmp/keep-this",
                "2026-05-16T03:00:00Z [ERROR] database_open_failed db=/tmp/keep-this/.cdidx/codeindex.db",
                "2026-05-16T03:00:00Z [INFO] config_file_loaded path=/tmp/keep-this/.cdidx/config.json",
                "2026-05-16T03:00:00Z [INFO] args=index .",
                ""));

        var previousLogDir = Environment.GetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR");
        Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", logDir);
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--log-lines", "20",
                "--include-args",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            var logText = Encoding.UTF8.GetString(entries["log/stderr-recent.log"]);
            Assert.Contains("args=index .", logText);
            Assert.Contains("cwd=[redacted]", logText);
            Assert.Contains("process_path=[redacted]", logText);
            Assert.Contains("base_dir=[redacted]", logText);
            Assert.Contains("db=[redacted]", logText);
            Assert.Contains("path=[redacted]", logText);
            Assert.DoesNotContain("/tmp/keep-this", logText);
            Assert.DoesNotContain("/tmp/cdidx", logText);
            Assert.DoesNotContain("/tmp/cdidx-store", logText);
            Assert.DoesNotContain("/tmp/keep-this/.cdidx/codeindex.db", logText);
            Assert.DoesNotContain("/tmp/keep-this/.cdidx/config.json", logText);
            Assert.DoesNotContain("args=[redacted]", logText);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CDIDX_GLOBAL_TOOL_LOG_DIR", previousLogDir);
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_JsonMode_PrintsSummaryEnvelope()
    {
        var workDir = CreateWorkDir();
        try
        {
            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, json) = RunAndCaptureJson([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--no-log",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(ReportCommandRunner.RedactedPlaceholder, json.GetProperty("output_path").GetString());
            Assert.True(json.GetProperty("files").GetInt32() >= 4);
            Assert.False(json.GetProperty("log_included").GetBoolean());
            Assert.False(json.GetProperty("db_included").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_JsonMode_RedactsLocalSummaryPaths_Issue3554()
    {
        var workDir = CreateWorkDir();
        var dbPath = Path.Combine(workDir, "codeindex.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            SqliteConnection.ClearAllPools();

            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, json) = RunAndCaptureJson([
                "--output", output,
                "--db", dbPath,
                "--no-log",
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(ReportCommandRunner.RedactedPlaceholder, json.GetProperty("output_path").GetString());
            Assert.Equal(ReportCommandRunner.RedactedPlaceholder, json.GetProperty("db_path").GetString());
            Assert.True(json.GetProperty("db_included").GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void Run_JsonMode_RedactsFailureExceptionMessage_Issue3554()
    {
        var workDir = CreateWorkDir();
        try
        {
            var fileParent = Path.Combine(workDir, "not-a-directory");
            File.WriteAllText(fileParent, "blocking file");
            var output = Path.Combine(fileParent, "bundle.tgz");

            var (exitCode, json) = RunAndCaptureJson([
                "--output", output,
                "--db", Path.Combine(workDir, "missing.db"),
                "--json",
            ]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            var message = json.GetProperty("message").GetString();
            Assert.Contains("failed to build report:", message);
            Assert.Contains(ReportCommandRunner.RedactedPlaceholder, message);
            Assert.DoesNotContain(workDir, message);
            Assert.DoesNotContain(fileParent, message);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public void RedactSensitiveFields_RedactsCwdLine()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [INFO] cwd=/private/foo/secret-project");

        Assert.Contains("cwd=[redacted]", redacted);
        Assert.DoesNotContain("/private/foo/secret-project", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsProcessPathLine()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [INFO] process_path=/Users/example/.dotnet/tools/cdidx");

        Assert.Contains("process_path=[redacted]", redacted);
        Assert.DoesNotContain("/Users/example/.dotnet/tools/cdidx", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsBaseDirLine()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [INFO] base_dir=/Users/example/.dotnet/tools/.store/cdidx");

        Assert.Contains("base_dir=[redacted]", redacted);
        Assert.DoesNotContain("/Users/example/.dotnet/tools/.store/cdidx", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsDatabasePathLine()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [ERROR] database_open_failed db=/Users/example/project/.cdidx/codeindex.db");

        Assert.Contains("db=[redacted]", redacted);
        Assert.DoesNotContain("/Users/example/project/.cdidx/codeindex.db", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsConfigPathLine()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [INFO] config_file_loaded path=/Users/example/project/.cdidx/config.json");

        Assert.Contains("path=[redacted]", redacted);
        Assert.DoesNotContain("/Users/example/project/.cdidx/config.json", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsArgsLine()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [INFO] args=query \"SELECT * FROM secret\"");

        Assert.Contains("args=[redacted]", redacted);
        Assert.DoesNotContain("SELECT * FROM secret", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsJsonLifecycleMessage_Issue3554()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "{\"ts\":\"2026-05-16T03:00:00.000Z\",\"level\":\"INFO\",\"msg\":\"cwd=/Users/example/private db=/Users/example/private/.cdidx/codeindex.db path=/Users/example/private/.cdidx/config.json args=query \\\"SELECT * FROM secret\\\"\"}");

        using var document = JsonDocument.Parse(redacted);
        var message = document.RootElement.GetProperty("msg").GetString();
        Assert.Contains("cwd=[redacted]", message);
        Assert.Contains("db=[redacted]", message);
        Assert.Contains("path=[redacted]", message);
        Assert.Contains("args=[redacted]", message);
        Assert.DoesNotContain("/Users/example/private", redacted);
        Assert.DoesNotContain("SELECT * FROM secret", redacted);
    }

    [Fact]
    public void RedactLogLine_JsonIncludeArgsPreservesSafeArgsAndRedactsSecrets_Issue3554()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var redacted = ReportCommandRunner.RedactLogLine(
            $"{{\"ts\":\"2026-05-16T03:00:00.000Z\",\"level\":\"INFO\",\"msg\":\"args=query --literal safe --token={secret} /Users/example/private status=ok\"}}",
            includeArgs: true);

        using var document = JsonDocument.Parse(redacted);
        var message = document.RootElement.GetProperty("msg").GetString();
        Assert.Contains("args=query --literal safe --token=[redacted] [redacted]", message);
        Assert.Contains("status=ok", message);
        Assert.DoesNotContain(secret, redacted);
        Assert.DoesNotContain("/Users/example/private", redacted);
        Assert.DoesNotContain("args=[redacted]", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_PreservesFieldsAfterRedactedValues_Issue3554()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [INFO] cwd=/private/foo status=ok token=0123456789abcdef0123456789abcdef elapsed_ms=4 path=/tmp/config.json");

        Assert.Contains("cwd=[redacted]", redacted);
        Assert.Contains("status=ok", redacted);
        Assert.Contains("token=[redacted]", redacted);
        Assert.Contains("elapsed_ms=4", redacted);
        Assert.Contains("path=[redacted]", redacted);
        Assert.DoesNotContain("/private/foo", redacted);
        Assert.DoesNotContain("0123456789abcdef0123456789abcdef", redacted);
        Assert.DoesNotContain("/tmp/config.json", redacted);
    }

    [Fact]
    public void RedactLogLine_IncludeArgsPreservesSafeArgsAndRedactsSecrets_Issue3554()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var redacted = ReportCommandRunner.RedactLogLine(
            $"2026-05-16T03:00:00Z [INFO] args=query --literal safe --token={secret} /Users/example/private status=ok",
            includeArgs: true);

        Assert.Contains("args=query --literal safe --token=[redacted] [redacted]", redacted);
        Assert.Contains("status=ok", redacted);
        Assert.DoesNotContain(secret, redacted);
        Assert.DoesNotContain("/Users/example/private", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsNoKeyPathTokenAndUrlQuery_Issue3554()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            $"2026-05-16T03:00:00Z [ERROR] sample /Users/example/private {secret} https://example.test?query=user-content");

        Assert.Contains("[redacted]", redacted);
        Assert.Contains("https://example.test[redacted]", redacted);
        Assert.DoesNotContain("/Users/example/private", redacted);
        Assert.DoesNotContain(secret, redacted);
        Assert.DoesNotContain("query=user-content", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_RedactsPrefixAndSuffixOutsideKeyValues_Issue3554()
    {
        var redacted = ReportCommandRunner.RedactSensitiveFields(
            "2026-05-16T03:00:00Z [ERROR] prefix /Users/example/before cwd=/Users/example/private status=ok suffix /Users/example/after");

        Assert.Contains("cwd=[redacted]", redacted);
        Assert.Contains("status=ok", redacted);
        Assert.DoesNotContain("/Users/example/before", redacted);
        Assert.DoesNotContain("/Users/example/private", redacted);
        Assert.DoesNotContain("/Users/example/after", redacted);
    }

    [Fact]
    public void RedactSensitiveFields_LinesWithoutSensitiveKeysPassThrough()
    {
        var line = "2026-05-16T03:00:00Z [INFO] session_start pid=1 version=1.21.0";
        var redacted = ReportCommandRunner.RedactSensitiveFields(line);

        Assert.Equal(line, redacted);
    }

    [Fact]
    public void BuildSupportManifest_DoesNotReportSchemaTableOmissionAtExactLimit_Issue3555()
    {
        var bundle = new ReportBundle
        {
            DbIncluded = false,
            LogIncluded = false,
            SchemaTables = Enumerable.Range(0, ReportCommandRunner.MaxSchemaTables)
                .Select(i => new ReportSchemaTable($"table_{i:D3}", 0))
                .ToList(),
        };
        var options = new ReportCommandOptions { IncludeLog = false };

        var manifest = ReportCommandRunner.BuildSupportManifest(
            options,
            bundle,
            DateTimeOffset.UnixEpoch,
            ReportRedactionSummary.Empty);

        Assert.DoesNotContain("schema_tables_after_limit", manifest.Omissions.Schema);

        bundle.SchemaTablesTruncated = true;
        var truncatedManifest = ReportCommandRunner.BuildSupportManifest(
            options,
            bundle,
            DateTimeOffset.UnixEpoch,
            ReportRedactionSummary.Empty);

        Assert.Contains("schema_tables_after_limit", truncatedManifest.Omissions.Schema);
    }

    [Fact]
    public void Run_WithMetadataTargetSourceMissing_ManifestReadinessIsDegraded_Issue3555()
    {
        var workDir = CreateWorkDir();
        var dbPath = Path.Combine(workDir, "legacy-source.db");
        try
        {
            using (var db = new DbContext(dbPath))
                db.InitializeSchema();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = $"""
                    INSERT INTO files (path, lang, size, lines, checksum, modified)
                    VALUES ('src/Legacy.cs', 'csharp', 1, 1, 'legacy', '2026-01-01T00:00:00Z');
                    INSERT OR REPLACE INTO codeindex_meta (key, value)
                    VALUES ('{DbContext.GetMetadataTargetVersionMetaKey("csharp")}', '{DbContext.MetadataTargetVersion}');
                    PRAGMA foreign_keys = OFF;
                    ALTER TABLE symbols RENAME TO symbols_old;
                    CREATE TABLE symbols (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                        kind            TEXT,
                        sub_kind        TEXT,
                        name            TEXT,
                        name_folded     TEXT,
                        line            INTEGER,
                        start_line      INTEGER,
                        start_column    INTEGER,
                        end_line        INTEGER,
                        body_start_line INTEGER,
                        body_end_line   INTEGER,
                        signature       TEXT,
                        container_kind  TEXT,
                        container_name  TEXT,
                        container_qualified_name TEXT,
                        family_key      TEXT,
                        visibility      TEXT,
                        return_type     TEXT,
                        is_metadata_target INTEGER
                    );
                    DROP TABLE symbols_old;
                    PRAGMA foreign_keys = ON;
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var output = Path.Combine(workDir, "bundle.tgz");
            var (exitCode, _, _) = RunAndCaptureStreams([
                "--output", output,
                "--db", dbPath,
                "--no-log",
            ]);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            var entries = ReadTarGzEntries(output);
            using var manifest = ReadJsonEntry(entries, "support-manifest.json");
            var readiness = manifest.RootElement.GetProperty("readiness");

            Assert.True(
                readiness.TryGetProperty("csharp_metadata_target_ready", out var csharpMetadataTargetReady),
                readiness.GetRawText());
            Assert.False(readiness.TryGetProperty("c_sharp_metadata_target_ready", out _), readiness.GetRawText());
            Assert.False(csharpMetadataTargetReady.GetBoolean());
            Assert.True(JsonArrayContains(readiness.GetProperty("degraded_fields"), "csharp_metadata_target_ready"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(workDir);
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunAndCaptureStreams(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                var exitCode = ReportCommandRunner.Run(args, _jsonOptions, appVersion: "test");
                return (exitCode, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private (int ExitCode, JsonElement Json) RunAndCaptureJson(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                var exitCode = ReportCommandRunner.Run(args, _jsonOptions, appVersion: "test");
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private static string CreateWorkDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdidx_report_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for tests / テスト用ベストエフォート削除。
        }
    }

    private static Dictionary<string, byte[]> ReadTarGzEntries(string path)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var fileStream = File.OpenRead(path);
        using var gz = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile)
                continue;
            using var buffer = new MemoryStream();
            entry.DataStream?.CopyTo(buffer);
            entries[entry.Name] = buffer.ToArray();
        }
        return entries;
    }

    private static JsonDocument ReadJsonEntry(Dictionary<string, byte[]> entries, string name)
        => JsonDocument.Parse(entries[name]);

    private static bool JsonArrayContains(JsonElement array, string value)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (string.Equals(item.GetString(), value, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static Dictionary<string, UnixFileMode> ReadTarGzEntryModes(string path)
    {
        var entries = new Dictionary<string, UnixFileMode>(StringComparer.Ordinal);
        using var fileStream = File.OpenRead(path);
        using var gz = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile)
                continue;
            entries[entry.Name] = entry.Mode;
        }
        return entries;
    }
}
