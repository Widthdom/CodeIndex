using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class StatusMetadataOutputIssue5161Tests
{
    private const string TruncationMarker = "... <truncated; original length";
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void RunStatus_ReportsUnavailableSubdocumentWithoutFailingJson_Issue5161()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_metadata_diagnostic_5161");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.LastIndexRunModeMetaKey, "rebuild");
                writer.SetMeta(
                    DbContext.LastIndexRunRebuildReclaimMetaKey,
                    new string('x', StatusMetadataLimits.MaxRawUtf8Bytes + 1));
            }
            PrepareImmutableRead(dbPath);

            var result = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--read-only", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, result.ExitCode);
            Assert.Equal(string.Empty, result.Stderr);
            using var document = JsonDocument.Parse(result.Stdout);
            var root = document.RootElement;
            var lastRun = root.GetProperty("last_index_run");
            Assert.Equal("rebuild", lastRun.GetProperty("mode").GetString());
            Assert.False(lastRun.TryGetProperty("rebuild_reclaim", out _));
            var diagnostic = Assert.Single(
                root.GetProperty("status_metadata_diagnostics").EnumerateArray());
            Assert.Equal("last_index_run.rebuild_reclaim", diagnostic.GetProperty("field").GetString());
            Assert.Equal("raw_size_exceeded", diagnostic.GetProperty("reason").GetString());
            Assert.Equal(
                StatusMetadataLimits.MaxRawUtf8Bytes,
                diagnostic.GetProperty("max_utf8_bytes").GetInt32());
            Assert.Equal(
                StatusMetadataLimits.MaxRawUtf8Bytes + 1L,
                diagnostic.GetProperty("observed_utf8_bytes").GetInt64());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunStatus_PreservesJsonAndSanitizesHumanFailureDiagnostics_Issue5161()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_status_metadata_5161");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var file = CreateControlledValue("file", 150);
            var category = CreateControlledValue("category", 125);
            var phase = CreateControlledValue("phase", 125);
            var detail = CreateControlledValue("detail", 150);
            var recoveryHint = CreateControlledValue("recovery", 150);
            SetFailedStatus(dbPath, file, category, phase, detail, recoveryHint);
            PrepareImmutableRead(dbPath);

            var json = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--read-only", "--json"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, json.ExitCode);
            Assert.Equal(string.Empty, json.Stderr);
            using (var document = JsonDocument.Parse(json.Stdout))
            {
                var root = document.RootElement;
                var failed = root.GetProperty("last_failed_or_partial_index_run");
                var firstFailure = failed.GetProperty("file_errors")[0];
                Assert.Equal(file, firstFailure.GetProperty("file").GetString());
                Assert.Equal(category, firstFailure.GetProperty("category").GetString());
                Assert.Equal(phase, firstFailure.GetProperty("phase").GetString());
                Assert.Equal(detail, firstFailure.GetProperty("detail").GetString());
                Assert.Equal(recoveryHint, failed.GetProperty("recovery_hint").GetString());
                Assert.False(root.TryGetProperty("status_metadata_diagnostics", out _));
                Assert.True(root.GetProperty("sqlite_connection_policy").GetProperty("immutable_uri").GetBoolean());
            }

            var human = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--read-only"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, human.ExitCode);
            var humanOutput = human.Stdout + human.Stderr;
            var firstFailureLine = Assert.Single(
                humanOutput.Split(Environment.NewLine, StringSplitOptions.None)
                    .Where(line => line.Contains("First failure:", StringComparison.Ordinal)));
            Assert.Equal(4, CountOccurrences(firstFailureLine, TruncationMarker));
            Assert.DoesNotContain('\r', firstFailureLine);
            Assert.DoesNotContain('\n', firstFailureLine);
            Assert.DoesNotContain('\t', firstFailureLine);
            Assert.DoesNotContain('\u001b', firstFailureLine);
            Assert.DoesNotContain('\u0001', firstFailureLine);
            var hintLine = Assert.Single(
                humanOutput.Split(Environment.NewLine, StringSplitOptions.None).Where(line =>
                    line.TrimStart().StartsWith("Hint", StringComparison.Ordinal)
                    && line.Contains(TruncationMarker, StringComparison.Ordinal)));
            Assert.Contains(TruncationMarker, hintLine, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', hintLine);
            Assert.DoesNotContain('\n', hintLine);
            Assert.DoesNotContain('\t', hintLine);
            Assert.DoesNotContain('\u001b', hintLine);
            Assert.DoesNotContain('\u0001', hintLine);

            SetFailedStatus(
                dbPath,
                "src/App.cs",
                "file_read_error",
                "reading",
                "access denied",
                "Retry indexing.");
            PrepareImmutableRead(dbPath);
            var shortHuman = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--read-only"],
                _jsonOptions));
            var shortOutput = shortHuman.Stdout + shortHuman.Stderr;
            Assert.Contains(
                "First failure: src/App.cs (file_read_error, reading): access denied",
                shortOutput,
                StringComparison.Ordinal);
            Assert.Contains("Retry indexing.", shortOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(TruncationMarker, shortOutput, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string CreateControlledValue(string prefix, int length)
    {
        var rawPrefix = prefix + "\r\n\t\u001b\u0001-";
        return rawPrefix + new string('x', length - rawPrefix.Length);
    }

    private static void SetFailedStatus(
        string dbPath,
        string file,
        string category,
        string phase,
        string detail,
        string recoveryHint)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkIndexIncomplete(["file_index_error"]);
        writer.SetMeta(DbContext.LastFailedIndexRunStatusMetaKey, "failed");
        writer.SetMeta(DbContext.LastFailedIndexRunModeMetaKey, "incremental");
        writer.SetMeta(DbContext.LastFailedIndexRunRecoveryHintMetaKey, recoveryHint);
        writer.SetMeta(
            DbContext.LastFailedIndexRunFileErrorsMetaKey,
            JsonSerializer.Serialize(
                new List<StatusIndexFileError>
                {
                    new()
                    {
                        File = file,
                        Category = category,
                        Phase = phase,
                        Detail = detail,
                    },
                },
                StatusMetadataJsonContext.Default.ListStatusIndexFileError));
    }

    private static void PrepareImmutableRead(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using (var checkpoint = command.ExecuteReader())
            {
                Assert.True(checkpoint.Read());
                Assert.Equal(0L, checkpoint.GetInt64(0));
            }
            command.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", command.ExecuteScalar()?.ToString(), ignoreCase: true);
        }
        SqliteConnection.ClearAllPools();
    }

    private static int CountOccurrences(string value, string search)
        => value.Split(search, StringSplitOptions.None).Length - 1;
}
