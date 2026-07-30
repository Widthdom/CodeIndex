using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class DiffCommandRunnerTests
{
    private const int LargeDiffFieldLength = 4096;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Run_JsonDiffReportsSyntheticDatabaseDrift_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: true);

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--json", "--limit", "5"]);
            var (textExitCode, textOutput) = RunWithCapturedOut([leftDb, rightDb, "--limit", "5"]);

            Assert.Equal(1, exitCode);
            Assert.Equal(1, textExitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("different", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("identical").GetBoolean());
            Assert.Equal(50, root.GetProperty("summary").GetProperty("left_file_count").GetInt64());
            Assert.Equal(51, root.GetProperty("summary").GetProperty("right_file_count").GetInt64());
            Assert.Equal(1, root.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Contains(
                root.GetProperty("files_only_in_right").EnumerateArray(),
                item => item.GetString() == "src/Extra.cs");
            var summary = root.GetProperty("summary");
            Assert.Contains(
                "data:file_rows_changed",
                summary.GetProperty("difference_reasons").EnumerateArray().Select(item => item.GetString()));
            Assert.Contains("mode   : semantic", textOutput, StringComparison.Ordinal);
            Assert.Contains("data (included):", textOutput, StringComparison.Ordinal);
            Assert.Contains("file_rows_changed", textOutput, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_OversizedFileUriQueryReturnsBoundedErrorBeforeReadingHeaders_Issue3140()
    {
        var dbUri = "file:///tmp/codeindex.db?" + new string('a', SqliteFileUri.MaxQueryLength + 1);

        var (exitCode, stdout, stderr) = RunWithCapturedStreams([dbUri, "/tmp/missing-codeindex.db"]);

        Assert.Equal(3, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("invalid database file URI", stderr);
        Assert.Contains($"SQLite file URI query length exceeds {SqliteFileUri.MaxQueryLength}", stderr);
        Assert.Contains("valid SQLite file URIs", stderr);
        Assert.DoesNotContain(new string('a', SqliteFileUri.MaxDiagnosticValueLength + 1), stderr);
    }

    [Fact]
    public void Run_JsonFileUrisReportOriginalUriPaths_Issue3221()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_diff_uri_json");
        try
        {
            var dbPath = SeedDb(root, includeExtraFile: false);
            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

            var (exitCode, output) = RunWithCapturedOut([dbUri, dbUri, "--json"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal(dbUri, document.RootElement.GetProperty("left_db").GetString());
            Assert.Equal(dbUri, document.RootElement.GetProperty("right_db").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Run_TextFileUrisReportOriginalUriPaths_Issue3221()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_diff_uri_text");
        try
        {
            var dbPath = SeedDb(root, includeExtraFile: false);
            var dbUri = new Uri(dbPath).AbsoluteUri + "?immutable=1";

            var (exitCode, output) = RunWithCapturedOut([dbUri, dbUri]);

            Assert.Equal(0, exitCode);
            Assert.Contains($"left   : {dbUri}", output);
            Assert.Contains($"right  : {dbUri}", output);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Run_LimitZeroStillDetectsDatabaseDrift_Issue2885()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_limit_zero_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_limit_zero_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: true);

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--json", "--limit", "0"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("different", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("identical").GetBoolean());
            Assert.Equal(0, root.GetProperty("files_only_in_left").GetArrayLength());
            Assert.Equal(0, root.GetProperty("files_only_in_right").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void ParseArgs_LimitAcceptsMaximum_Issue3162()
    {
        var options = DiffCommandRunner.ParseArgs(["left.db", "right.db", "--limit", $"{DiffCommandRunner.MaxDiffLimit}"]);

        Assert.Equal(DiffCommandRunner.MaxDiffLimit, options.Limit);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_LimitRejectsValueAboveMaximum_Issue3162()
    {
        var aboveMaximum = $"{DiffCommandRunner.MaxDiffLimit + 1}";
        var options = DiffCommandRunner.ParseArgs(["left.db", "right.db", "--limit", aboveMaximum]);

        Assert.InRange(options.Limit, 0, DiffCommandRunner.MaxDiffLimit);
        Assert.Equal("--limit must be less than or equal to 10000", options.ParseError);
    }

    [Fact]
    public void ParseArgs_OffsetAcceptsNonNegativePagingValue_Issue4714()
    {
        var options = DiffCommandRunner.ParseArgs(["left.db", "right.db", "--limit", "5", "--offset", "10"]);

        Assert.Equal(5, options.Limit);
        Assert.Equal(10, options.Offset);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_JsonBudgetAndContentFlagsEnforceDetailedContract_Issue4859()
    {
        var minimum = DiffCommandRunner.ParseArgs(
            ["left.db", "right.db", "--json", "--detailed", "--max-json-bytes", $"{DiffCommandRunner.MinDiffJsonBytes}"]);
        var belowMinimum = DiffCommandRunner.ParseArgs(
            ["left.db", "right.db", "--json", "--detailed", "--max-json-bytes", $"{DiffCommandRunner.MinDiffJsonBytes - 1}"]);
        var contentWithoutDetailedJson = DiffCommandRunner.ParseArgs(
            ["left.db", "right.db", "--include-content"]);

        Assert.Equal(DiffCommandRunner.MinDiffJsonBytes, minimum.MaxJsonBytes);
        Assert.Null(minimum.ParseError);
        Assert.Equal(
            $"--max-json-bytes must be at least {DiffCommandRunner.MinDiffJsonBytes}",
            belowMinimum.ParseError);
        Assert.Equal(
            "--include-content requires --detailed --json and cannot be combined with --summary-only",
            contentWithoutDetailedJson.ParseError);
    }

    [Fact]
    public void Run_CategorizesNoOpTelemetryAndReadinessAcrossModes_Issue4884()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_telemetry_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_telemetry_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: false);
            var sharedProjectRoot = Path.GetFullPath(leftRoot);
            SetMeta(leftDb, DbContext.IndexedProjectRootMetaKey, sharedProjectRoot);
            SetMeta(rightDb, DbContext.IndexedProjectRootMetaKey, sharedProjectRoot);
            SetMeta(rightDb, "indexed_head_timestamp", "2026-07-29T01:02:03Z");
            SetMeta(rightDb, "last_index_run_started_at", "2026-07-29T01:02:00Z");
            SetMeta(rightDb, "last_index_run_duration_ms", "123");
            SetMeta(rightDb, "last_index_run_mode", "incremental");
            SetMeta(rightDb, "last_index_run_bytes_read", "0");
            SetMeta(rightDb, DbContext.LastFullScanElapsedMsMetaKey, "456");
            SetMeta(rightDb, DbWriter.FtsLastOptimizedAtMetaKey, "2026-07-29T01:02:04Z");
            SetMeta(rightDb, DbWriter.FtsLastOptimizeDurationMsMetaKey, "789");
            SetMeta(rightDb, DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey, "3");
            SetMeta(rightDb, DbWriter.FtsIncrementalWritesSinceMergeMetaKey, "4");

            var (semanticExitCode, semanticOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--summary-only"]);
            var (detailedExitCode, detailedOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--limit", "20"]);
            var (telemetryExitCode, telemetryOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--summary-only", "--include-telemetry"]);
            var (telemetryDetailedExitCode, telemetryDetailedOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--include-telemetry", "--limit", "20"]);

            Assert.Equal(0, semanticExitCode);
            Assert.Equal(0, detailedExitCode);
            using (var semanticDocument = JsonDocument.Parse(semanticOutput))
            {
                var semantic = semanticDocument.RootElement;
                Assert.True(semantic.GetProperty("identical").GetBoolean());
                Assert.Equal("semantic", semantic.GetProperty("summary").GetProperty("comparison_mode").GetString());
                Assert.Equal(0, semantic.GetProperty("summary").GetProperty("difference_reason_count").GetInt32());
                var telemetry = GetCategory(semantic, "volatile_telemetry");
                Assert.True(telemetry.GetProperty("different").GetBoolean());
                Assert.False(telemetry.GetProperty("included").GetBoolean());
                Assert.Equal(
                    "volatile_telemetry_metadata_changed",
                    Assert.Single(telemetry.GetProperty("reasons").EnumerateArray()).GetString());
            }
            using (var detailedDocument = JsonDocument.Parse(detailedOutput))
            {
                Assert.Empty(GetRecords(detailedDocument.RootElement, "volatile_telemetry_metadata"));
            }

            Assert.Equal(1, telemetryExitCode);
            Assert.Equal(1, telemetryDetailedExitCode);
            using (var telemetryDocument = JsonDocument.Parse(telemetryOutput))
            {
                var telemetry = telemetryDocument.RootElement;
                Assert.False(telemetry.GetProperty("identical").GetBoolean());
                Assert.Equal(
                    "semantic_with_telemetry",
                    telemetry.GetProperty("summary").GetProperty("comparison_mode").GetString());
                Assert.Contains(
                    "volatile_telemetry:volatile_telemetry_metadata_changed",
                    telemetry.GetProperty("summary").GetProperty("difference_reasons")
                        .EnumerateArray()
                        .Select(item => item.GetString()));
            }
            using (var telemetryDetailedDocument = JsonDocument.Parse(telemetryDetailedOutput))
            {
                Assert.Equal(
                    10,
                    GetRecords(telemetryDetailedDocument.RootElement, "volatile_telemetry_metadata").Count);
            }

            SetMeta(leftDb, "hotspot_family_version", "left-readiness");
            SetMeta(rightDb, "hotspot_family_version", "right-readiness");
            SetMeta(leftDb, DbContext.IndexCompletenessMetaKey, "complete");
            SetMeta(rightDb, DbContext.IndexCompletenessMetaKey, "incomplete");
            SetMeta(leftDb, DbContext.IndexIncompleteReasonsMetaKey, null);
            SetMeta(rightDb, DbContext.IndexIncompleteReasonsMetaKey, """["symbols_only"]""");
            SetMeta(leftDb, DbContext.ReferenceIdentityContractVersionMetaKey, "5");
            SetMeta(rightDb, DbContext.ReferenceIdentityContractVersionMetaKey, "6");
            var (readinessExitCode, readinessOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--summary-only"]);
            var (dataOnlyExitCode, dataOnlyOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--summary-only", "--data-only"]);

            Assert.Equal(1, readinessExitCode);
            using (var readinessDocument = JsonDocument.Parse(readinessOutput))
            {
                Assert.Contains(
                    "readiness_provenance:readiness_provenance_metadata_changed",
                    readinessDocument.RootElement.GetProperty("summary").GetProperty("difference_reasons")
                        .EnumerateArray()
                        .Select(item => item.GetString()));
            }
            Assert.Equal(0, dataOnlyExitCode);
            using (var dataOnlyDocument = JsonDocument.Parse(dataOnlyOutput))
            {
                var dataOnly = dataOnlyDocument.RootElement;
                Assert.True(dataOnly.GetProperty("identical").GetBoolean());
                Assert.Equal("data_only", dataOnly.GetProperty("summary").GetProperty("comparison_mode").GetString());
                var readiness = GetCategory(dataOnly, "readiness_provenance");
                Assert.True(readiness.GetProperty("different").GetBoolean());
                Assert.False(readiness.GetProperty("included").GetBoolean());
            }

            var incompatible = DiffCommandRunner.ParseArgs(
                [leftDb, rightDb, "--data-only", "--include-telemetry"]);
            Assert.Equal("--data-only cannot be combined with --include-telemetry", incompatible.ParseError);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_PagedModesOnlyAdvertiseValidAdvancingContinuations_Issue4859()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_continuation_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_continuation_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            for (var i = 0; i < 3; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    leftDb,
                    $"src/OnlyInLeft{i}.cs",
                    "csharp",
                    $"public class OnlyInLeft{i} {{ }}");
            }

            var (sampleExitCode, sampleOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--limit", "1"]);
            var (detailedTextExitCode, detailedTextOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--detailed", "--limit", "1"]);

            Assert.Equal(1, sampleExitCode);
            using var sampleDocument = JsonDocument.Parse(sampleOutput);
            Assert.True(sampleDocument.RootElement.GetProperty("has_more").GetBoolean());
            Assert.Equal(1, sampleDocument.RootElement.GetProperty("next_offset").GetInt32());
            Assert.Equal(1, detailedTextExitCode);
            Assert.Contains("rerun with --offset 1", detailedTextOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("rerun with --cursor", detailedTextOutput, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonRedactsSensitiveTextUntilContentIsExplicitlyIncluded_Issue4859()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_private_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_private_right");
        try
        {
            const string privatePath = "src/customer-acme-api-key.cs";
            const string secret = "sk-test-super-secret-value";
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(
                leftDb,
                privatePath,
                "csharp",
                $"public static class Secret {{ public const string Value = \"{secret}\"; }}");

            var (redactedExitCode, redactedOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--limit", "100"]);

            Assert.Equal(1, redactedExitCode);
            using var redactedDocument = JsonDocument.Parse(redactedOutput);
            var redactedRoot = redactedDocument.RootElement;
            Assert.False(redactedRoot.GetProperty("content_included").GetBoolean());
            Assert.Equal("redacted_hashes", redactedRoot.GetProperty("content_policy").GetString());
            Assert.DoesNotContain(privatePath, redactedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, redactedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(leftDb, redactedOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(rightDb, redactedOutput, StringComparison.Ordinal);
            var fileRecord = Assert.Single(GetRecords(redactedRoot, "file", "left"));
            var pathField = GetField(fileRecord, "path");
            Assert.True(pathField.GetProperty("redacted").GetBoolean());
            Assert.False(pathField.TryGetProperty("value", out _));
            Assert.Equal(64, pathField.GetProperty("sha256").GetString()?.Length);
            Assert.True(pathField.GetProperty("byte_length").GetInt64() > 0);

            const int byteBudget = 65_536;
            var (includedExitCode, includedOutput) = RunWithCapturedOut(
                [
                    leftDb,
                    rightDb,
                    "--json",
                    "--detailed",
                    "--include-content",
                    "--limit",
                    "100",
                    "--max-json-bytes",
                    $"{byteBudget}",
                ]);

            Assert.Equal(1, includedExitCode);
            Assert.InRange(Encoding.UTF8.GetByteCount(includedOutput), 1, byteBudget);
            using var includedDocument = JsonDocument.Parse(includedOutput);
            var includedRoot = includedDocument.RootElement;
            Assert.True(includedRoot.GetProperty("content_included").GetBoolean());
            Assert.Equal("included", includedRoot.GetProperty("content_policy").GetString());
            Assert.Contains(privatePath, includedOutput, StringComparison.Ordinal);
            Assert.Contains(secret, includedOutput, StringComparison.Ordinal);
            var includedPath = GetField(
                Assert.Single(GetRecords(includedRoot, "file", "left")),
                "path");
            Assert.False(includedPath.GetProperty("redacted").GetBoolean());
            Assert.Equal(privatePath, includedPath.GetProperty("value").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonStopsAtRecordBoundaryAndCursorResumes_Issue4859()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_bounded_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_bounded_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            for (var i = 0; i < 30; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    leftDb,
                    $"src/PrivateFile{i:00}.cs",
                    "csharp",
                    $"public static class PrivateFile{i:00} {{ public static string Value => \"{new string('x', 256)}\"; }}");
            }

            const int byteBudget = 8_192;
            var (firstExitCode, firstOutput) = RunWithCapturedOut(
                [
                    leftDb,
                    rightDb,
                    "--json",
                    "--detailed",
                    "--limit",
                    "100",
                    "--max-json-bytes",
                    $"{byteBudget}",
                ]);

            Assert.Equal(1, firstExitCode);
            Assert.InRange(Encoding.UTF8.GetByteCount(firstOutput), 1, byteBudget);
            using var firstDocument = JsonDocument.Parse(firstOutput);
            var first = firstDocument.RootElement;
            Assert.Equal("max_json_bytes", first.GetProperty("truncation_reason").GetString());
            Assert.True(first.GetProperty("truncated").GetBoolean());
            Assert.True(first.GetProperty("has_more").GetBoolean());
            Assert.True(first.GetProperty("returned_count").GetInt32() > 0);
            Assert.True(first.GetProperty("first_omitted_record_bytes").GetInt32() > 0);
            Assert.Equal(
                first.GetProperty("total_count").GetInt64(),
                first.GetProperty("returned_count").GetInt32() + first.GetProperty("omitted_count").GetInt64());
            var replay = first.GetProperty("replay");
            Assert.True(replay.GetProperty("database_arguments_required").GetBoolean());
            var nextCursor = replay.GetProperty("next_cursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(nextCursor));
            var replayArguments = replay.GetProperty("next_page_arguments")
                .EnumerateArray()
                .Select(item => item.GetString()!)
                .ToArray();

            var (secondExitCode, secondOutput) = RunWithCapturedOut([leftDb, rightDb, .. replayArguments]);

            Assert.Equal(1, secondExitCode);
            Assert.InRange(Encoding.UTF8.GetByteCount(secondOutput), 1, byteBudget);
            using var secondDocument = JsonDocument.Parse(secondOutput);
            var second = secondDocument.RootElement;
            Assert.Equal(nextCursor, second.GetProperty("current_cursor").GetString());
            var firstIdentities = first.GetProperty("records")
                .EnumerateArray()
                .Select(record => record.GetProperty("identity_sha256").GetString())
                .ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain(
                second.GetProperty("records").EnumerateArray(),
                record => firstIdentities.Contains(record.GetProperty("identity_sha256").GetString()));

            var (mismatchExitCode, mismatchOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--include-content", "--cursor", nextCursor!]);
            Assert.Equal(CommandExitCodes.UsageError, mismatchExitCode);
            using var mismatchDocument = JsonDocument.Parse(mismatchOutput);
            Assert.Equal("error", mismatchDocument.RootElement.GetProperty("status").GetString());

            TestProjectHelper.InsertIndexedFile(
                leftDb,
                "src/AfterCursorMutation.cs",
                "csharp",
                "public static class AfterCursorMutation { }");
            var (staleExitCode, staleOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--cursor", nextCursor!]);

            Assert.Equal(CommandExitCodes.UsageError, staleExitCode);
            using var staleDocument = JsonDocument.Parse(staleOutput);
            Assert.Equal("error", staleDocument.RootElement.GetProperty("status").GetString());
            Assert.Contains(
                "no longer matches the selected database contents",
                staleDocument.RootElement.GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void CompareDatabases_PrettyMaterializationUsesActiveJsonSize_Issue4859()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_pretty_bound_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_pretty_bound_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            for (var i = 0; i < 30; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    leftDb,
                    $"src/PrettyBound{i:00}.cs",
                    "csharp",
                    $"public class PrettyBound{i:00} {{ public string Value => \"{new string('x', 128)}\"; }}");
            }

            var prettyOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true,
            };
            const int byteBudget = DiffCommandRunner.MinDiffJsonBytes;
            var complete = DiffCommandRunner.CompareDatabases(
                new DiffCommandOptions
                {
                    LeftDb = leftDb,
                    RightDb = rightDb,
                    Json = true,
                    Detailed = true,
                    Limit = 100,
                    MaxJsonBytes = DiffCommandRunner.MaxDiffJsonBytes,
                },
                prettyOptions,
                CancellationToken.None);
            var expectedRetained = GetMaterializedRecordCount(
                complete.Records ?? [],
                byteBudget,
                CliJsonSerializerContextFactory.Create(prettyOptions));
            var compactRetained = GetMaterializedRecordCount(
                complete.Records ?? [],
                byteBudget,
                CliJsonSerializerContext.Default);

            var bounded = DiffCommandRunner.CompareDatabases(
                new DiffCommandOptions
                {
                    LeftDb = leftDb,
                    RightDb = rightDb,
                    Json = true,
                    Detailed = true,
                    Limit = 100,
                    MaxJsonBytes = byteBudget,
                },
                prettyOptions,
                CancellationToken.None);

            Assert.True(expectedRetained < complete.Records!.Count);
            Assert.True(expectedRetained < compactRetained);
            Assert.Equal(expectedRetained, bounded.Records!.Count);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void CompareDatabases_EmbeddedSchemaMismatchOmitsCursorSelectionMetadata_Issue4859()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_embedded_schema_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_embedded_schema_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            ExecuteNonQuery(rightDb, "PRAGMA user_version = 999");

            var result = DiffCommandRunner.CompareDatabases(
                leftDb,
                rightDb,
                limit: 10,
                offset: 0,
                detailed: true,
                CancellationToken.None,
                emitCursorMetadata: false);

            Assert.Equal("schema_mismatch", result.Status);
            Assert.Null(result.SelectionFingerprint);
            Assert.Null(result.CurrentCursor);
            Assert.Null(result.NextCursor);
            Assert.Null(result.Replay);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonLimitZeroWithholdsNonAdvancingReplay_Issue4859()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_zero_limit_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_zero_limit_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(
                leftDb,
                "src/OnlyInLeft.cs",
                "csharp",
                "public class OnlyInLeft { }");

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--limit", "0"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.True(root.GetProperty("has_more").GetBoolean());
            Assert.Empty(root.GetProperty("records").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("next_offset").ValueKind);
            Assert.False(root.TryGetProperty("next_cursor", out _));
            var replay = root.GetProperty("replay");
            Assert.False(replay.TryGetProperty("next_cursor", out _));
            Assert.False(replay.TryGetProperty("next_page_arguments", out _));
            Assert.Contains(
                root.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("message").GetString()?.Contains(
                    "positive --limit",
                    StringComparison.Ordinal) == true);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_JsonModesHonorMinimumExplicitBudget_Issue4859()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_diff_minimum_budget");
        try
        {
            var db = TestProjectHelper.CreateProjectDb(root);
            const int byteBudget = DiffCommandRunner.MinDiffJsonBytes;

            var (detailedExitCode, detailedOutput) = RunWithCapturedOut(
                [db, db, "--json", "--detailed", "--max-json-bytes", $"{byteBudget}"]);
            var (sampleExitCode, sampleOutput) = RunWithCapturedOut(
                [db, db, "--json", "--max-json-bytes", $"{byteBudget}"]);
            var (summaryExitCode, summaryOutput) = RunWithCapturedOut(
                [db, db, "--summary-only", "--max-json-bytes", $"{byteBudget}"]);
            var oversizedUnsupportedOption = "--" + new string('x', byteBudget * 2);
            var (parseErrorExitCode, parseErrorOutput) = RunWithCapturedOut(
                [
                    db,
                    db,
                    "--json",
                    oversizedUnsupportedOption,
                    "--max-json-bytes",
                    $"{byteBudget}",
                ]);

            Assert.Equal(0, detailedExitCode);
            Assert.Equal(0, sampleExitCode);
            Assert.Equal(0, summaryExitCode);
            Assert.Equal(CommandExitCodes.UsageError, parseErrorExitCode);
            Assert.InRange(Encoding.UTF8.GetByteCount(detailedOutput), 1, byteBudget);
            Assert.InRange(Encoding.UTF8.GetByteCount(sampleOutput), 1, byteBudget);
            Assert.InRange(Encoding.UTF8.GetByteCount(summaryOutput), 1, byteBudget);
            Assert.InRange(Encoding.UTF8.GetByteCount(parseErrorOutput), 1, byteBudget);
            using var detailedDocument = JsonDocument.Parse(detailedOutput);
            using var sampleDocument = JsonDocument.Parse(sampleOutput);
            using var summaryDocument = JsonDocument.Parse(summaryOutput);
            using var parseErrorDocument = JsonDocument.Parse(parseErrorOutput);
            Assert.Equal("identical", detailedDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("identical", sampleDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("identical", summaryDocument.RootElement.GetProperty("status").GetString());
            Assert.Equal("error", parseErrorDocument.RootElement.GetProperty("status").GetString());
            Assert.DoesNotContain(oversizedUnsupportedOption, parseErrorOutput, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Run_DetailedJsonReportsLimitedSymbolRows_Issue2885()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_detailed_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_detailed_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            const string content = "public class Same { public void Run() { } }";
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", content);
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", content);
            InsertSyntheticMethodSymbol(leftDb, "src/Same.cs", "LeftOnly", "void LeftOnly()");
            InsertSyntheticMethodSymbol(rightDb, "src/Same.cs", "RightOnly", "void RightOnly()");

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--include-content", "--limit", "2"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var records = GetRecords(root, "symbol");
            Assert.Collection(
                records,
                record =>
                {
                    Assert.Equal("left", record.GetProperty("side").GetString());
                    Assert.Equal("LeftOnly", GetField(record, "name").GetProperty("value").GetString());
                },
                record =>
                {
                    Assert.Equal("right", record.GetProperty("side").GetString());
                    Assert.Equal("RightOnly", GetField(record, "name").GetProperty("value").GetString());
                });
            Assert.All(records, record => Assert.Equal(64, record.GetProperty("identity_sha256").GetString()?.Length));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonPagesReferenceAndChunkDrift_Issue4714()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_edge_chunk_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_edge_chunk_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            for (var i = 0; i < 3; i++)
            {
                var content = $"public class Same{i} {{ public void Run() {{ Run(); }} }}";
                TestProjectHelper.InsertIndexedFile(leftDb, $"src/Same{i}.cs", "csharp", content);
                TestProjectHelper.InsertIndexedFile(rightDb, $"src/Same{i}.cs", "csharp", content);
            }
            ExecuteNonQuery(rightDb, "UPDATE chunks SET content = content || ' // right'");
            ExecuteNonQuery(rightDb, "UPDATE symbol_references SET context = COALESCE(context, '') || ' // right'");

            var (firstExitCode, firstOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--limit", "1"]);

            Assert.Equal(1, firstExitCode);
            using var firstDocument = JsonDocument.Parse(firstOutput);
            var first = firstDocument.RootElement;
            var firstRecord = Assert.Single(first.GetProperty("records").EnumerateArray());
            Assert.Equal("reference", firstRecord.GetProperty("area").GetString());
            Assert.True(first.GetProperty("has_more").GetBoolean());
            Assert.Equal(1, first.GetProperty("next_offset").GetInt32());
            var nextCursor = first.GetProperty("next_cursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(nextCursor));
            Assert.Equal(
                nextCursor,
                first.GetProperty("replay").GetProperty("next_cursor").GetString());

            var (secondExitCode, secondOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--limit", "1", "--cursor", nextCursor!]);

            Assert.Equal(1, secondExitCode);
            using var secondDocument = JsonDocument.Parse(secondOutput);
            var second = secondDocument.RootElement;
            Assert.Equal(1, second.GetProperty("offset").GetInt32());
            var secondRecord = Assert.Single(second.GetProperty("records").EnumerateArray());
            Assert.Equal("reference", secondRecord.GetProperty("area").GetString());
            Assert.NotEqual(
                firstRecord.GetProperty("identity_sha256").GetString(),
                secondRecord.GetProperty("identity_sha256").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonDetectsResolvedReferenceGraphDrift_Issue4714()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_graph_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_graph_right");
        try
        {
            const string content = "public class Same { public void Run() { Run(); } }";
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", content);
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", content);
            const string seedResolvedGraphSql = """
                UPDATE symbol_references
                SET source_symbol_id = (SELECT id FROM symbols WHERE name = 'Run' LIMIT 1),
                    target_symbol_id = (SELECT id FROM symbols WHERE name = 'Run' LIMIT 1),
                    target_symbol_key = 'csharp' || char(31) || 'src/Same.cs' || char(31) || 'Same' || char(31) || 'Run',
                    resolution_candidate_count = 1,
                    resolution_state = 'resolved',
                    is_self_reference = 1;
                INSERT OR REPLACE INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
                SELECT r.id, s.id, 0
                FROM symbol_references r
                CROSS JOIN symbols s
                WHERE s.name = 'Run';
                """;
            ExecuteNonQuery(leftDb, seedResolvedGraphSql);
            ExecuteNonQuery(rightDb, seedResolvedGraphSql);
            ExecuteNonQuery(
                rightDb,
                """
                DELETE FROM symbol_reference_candidates;
                UPDATE symbol_references
                SET target_symbol_id = NULL,
                    target_symbol_key = NULL,
                    resolution_candidate_count = 0,
                    resolution_state = 'unresolved',
                    is_self_reference = 0;
                """);

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--limit", "5"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("different", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("identical").GetBoolean());
            Assert.Contains(
                "data:reference_rows_changed",
                root.GetProperty("summary").GetProperty("difference_reasons")
                    .EnumerateArray()
                    .Select(item => item.GetString()));
            var referenceRecords = GetRecords(root, "reference");
            Assert.Contains(referenceRecords, record => record.GetProperty("side").GetString() == "left");
            Assert.Contains(referenceRecords, record => record.GetProperty("side").GetString() == "right");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonReportsOperationalMetadataDrift_Issue4357()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_meta_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_meta_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: false);
            SetMeta(leftDb, DbContext.IndexedProjectRootMetaKey, Path.GetFullPath(leftRoot));
            SetMeta(rightDb, DbContext.IndexedProjectRootMetaKey, Path.GetFullPath(rightRoot));

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--data-only", "--limit", "5"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("identical", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("identical").GetBoolean());
            Assert.Equal("data_only", root.GetProperty("summary").GetProperty("comparison_mode").GetString());
            var readinessCategory = GetCategory(root, "readiness_provenance");
            Assert.True(readinessCategory.GetProperty("different").GetBoolean());
            Assert.False(readinessCategory.GetProperty("included").GetBoolean());
            var drift = Assert.Single(GetRecords(root, "readiness_provenance_metadata"));
            Assert.Equal("changed", drift.GetProperty("side").GetString());
            Assert.True(GetField(drift, "key").GetProperty("redacted").GetBoolean());
            Assert.True(GetField(drift, "left_value").GetProperty("redacted").GetBoolean());
            Assert.True(GetField(drift, "right_value").GetProperty("redacted").GetBoolean());
            Assert.DoesNotContain(Path.GetFullPath(leftRoot), output, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetFullPath(rightRoot), output, StringComparison.Ordinal);

            var additionalOperationalKeys = new[]
            {
                "indexed_follow_symlinks_policy",
                "indexed_head_commit",
                "indexed_head_commit_branch",
                "indexed_head_sha",
                "indexed_head_branch",
                "indexed_head_timestamp",
                "commit_scoped_fresh_head_sha",
                "workspace_path_case_sensitive",
                DbContext.CdidxWriterVersionMetaKey,
            };
            foreach (var key in additionalOperationalKeys)
            {
                SetMeta(leftDb, key, $"left-{key}");
                SetMeta(rightDb, key, $"right-{key}");
            }

            const int completePageBudget = 12_000;
            var (boundedExitCode, boundedOutput) = RunWithCapturedOut(
                [
                    leftDb,
                    rightDb,
                    "--json",
                    "--detailed",
                    "--data-only",
                    "--limit",
                    "100",
                    "--max-json-bytes",
                    $"{completePageBudget}",
                ]);

            Assert.Equal(0, boundedExitCode);
            Assert.InRange(Encoding.UTF8.GetByteCount(boundedOutput), 1, completePageBudget);
            using var boundedDocument = JsonDocument.Parse(boundedOutput);
            var boundedRoot = boundedDocument.RootElement;
            Assert.Equal(9, boundedRoot.GetProperty("total_count").GetInt64());
            Assert.Equal(9, boundedRoot.GetProperty("returned_count").GetInt32());
            Assert.False(boundedRoot.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonTreatsWriterVersionAsOperationalMetadataDrift_Issue4357()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_writer_meta_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_writer_meta_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: false);
            var sharedProjectRoot = Path.GetFullPath(leftRoot);
            SetMeta(leftDb, DbContext.IndexedProjectRootMetaKey, sharedProjectRoot);
            SetMeta(rightDb, DbContext.IndexedProjectRootMetaKey, sharedProjectRoot);
            SetMeta(leftDb, DbContext.CdidxWriterVersionMetaKey, "writer-left");
            SetMeta(rightDb, DbContext.CdidxWriterVersionMetaKey, "writer-right");

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--data-only", "--include-content", "--limit", "5"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("identical", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("identical").GetBoolean());
            var drift = Assert.Single(GetRecords(root, "readiness_provenance_metadata"));
            Assert.Equal("cdidx_writer_version", GetField(drift, "key").GetProperty("value").GetString());
            Assert.Equal("writer-left", GetField(drift, "left_value").GetProperty("value").GetString());
            Assert.Equal("writer-right", GetField(drift, "right_value").GetProperty("value").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_JsonReportsTruncatedDiagnosticWhenSamplesHitLimit_Issue3736()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_truncated_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_truncated_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/ExtraA.cs", "csharp", "public class ExtraA { }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/ExtraB.cs", "csharp", "public class ExtraB { }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--json", "--limit", "1"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("different", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("truncated").GetBoolean());
            Assert.Single(root.GetProperty("files_only_in_right").EnumerateArray());
            Assert.Contains(
                root.GetProperty("diagnostics").EnumerateArray(),
                item => item.GetProperty("code").GetString() == "diff_samples_truncated");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_JsonCancellationReturnsInterruptedError_Issue3736()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_diff_cancelled");
        try
        {
            var db = TestProjectHelper.CreateProjectDb(root);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var (exitCode, stdout, stderr) = RunWithCapturedStreams([db, db, "--json"], cts.Token);

            Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var rootElement = document.RootElement;
            Assert.Equal("error", rootElement.GetProperty("status").GetString());
            Assert.Equal(CommandErrorCodes.Interrupted, rootElement.GetProperty("error_code").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    [Fact]
    public void Run_StopsWhenDiffRowBudgetIsExceeded_Issue3834()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_row_budget_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_row_budget_right");
        var originalRowBudget = DiffCommandRunner.MaxDiffComparedRowsPerSideForTesting;
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            for (var i = 0; i < 11; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    leftDb,
                    $"src/File{i:00}.cs",
                    "csharp",
                    $"public class File{i:00} {{ }}");
                TestProjectHelper.InsertIndexedFile(
                    rightDb,
                    $"src/File{i:00}.cs",
                    "csharp",
                    $"public class File{i:00} {{ }}");
            }
            ExecuteNonQuery(
                rightDb,
                """
                INSERT INTO files (path, lang, size, lines, checksum, modified)
                VALUES ('src/C.cs', 'csharp', 1, 1, 'c', '2026-01-01T00:00:00Z');
                """);
            ExecuteNonQuery(
                rightDb,
                """
                UPDATE symbols
                SET name = 'Drifted', name_folded = 'drifted'
                WHERE id = (SELECT MIN(id) FROM symbols);
                """);

            var (differentExitCode, differentOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--summary-only"]);
            var (detailedExitCode, detailedOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--limit", "100"]);

            Assert.Equal(1, differentExitCode);
            Assert.Equal(1, detailedExitCode);
            using (var differentDocument = JsonDocument.Parse(differentOutput))
            using (var detailedDocument = JsonDocument.Parse(detailedOutput))
            {
                var summaryReasons = GetCategory(differentDocument.RootElement, "data")
                    .GetProperty("reasons")
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .ToArray();
                var detailedReasons = GetCategory(detailedDocument.RootElement, "data")
                    .GetProperty("reasons")
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .ToArray();
                Assert.Equal(["file_rows_changed"], summaryReasons);
                Assert.Equal(summaryReasons, detailedReasons);
                Assert.NotEmpty(GetRecords(detailedDocument.RootElement, "symbol"));
            }

            DiffCommandRunner.MaxDiffComparedRowsPerSideForTesting = 10;
            var (boundedExitCode, boundedOutput) = RunWithCapturedOut(
                [leftDb, rightDb, "--summary-only"]);
            Assert.Equal(1, boundedExitCode);
            Assert.Contains("data:file_rows_changed", boundedOutput, StringComparison.Ordinal);

            ExecuteNonQuery(rightDb, "DELETE FROM files WHERE path = 'src/C.cs';");
            var (exitCode, stdout, stderr) = RunWithCapturedStreams([leftDb, rightDb]);

            Assert.Equal(3, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("diff left row comparison exceeded the safety budget of 10 rows", stderr);
        }
        finally
        {
            DiffCommandRunner.MaxDiffComparedRowsPerSideForTesting = originalRowBudget;
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void ColumnExists_QuotesTableIdentifiersForPragmaInfo_Issue3834()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE \"odd \"\" table\" (\"odd col\" INTEGER)";
            command.ExecuteNonQuery();
        }
        var method = typeof(DiffCommandRunner).GetMethod("ColumnExists", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(DiffCommandRunner), "ColumnExists");

        var exists = Assert.IsType<bool>(method.Invoke(null, new object?[] { connection, "odd \" table", "odd col" }));

        Assert.True(exists);
    }

    [Fact]
    public void Run_DetailedJsonHashesLargeSymbolFieldsByDefault_Issue3163()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_large_field_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_large_field_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { }");

            var longSignature = new string('a', LargeDiffFieldLength);
            InsertSyntheticMethodSymbol(leftDb, "src/Same.cs", "Drifted", longSignature);

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--data-only", "--limit", "1"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var row = Assert.Single(GetRecords(document.RootElement, "symbol"));
            var signature = GetField(row, "signature");
            var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(longSignature))).ToLowerInvariant();
            Assert.True(signature.GetProperty("redacted").GetBoolean());
            Assert.Equal(expectedHash, signature.GetProperty("sha256").GetString());
            Assert.Equal(longSignature.Length, signature.GetProperty("byte_length").GetInt64());
            Assert.False(signature.TryGetProperty("value", out _));
            Assert.DoesNotContain(longSignature, output, StringComparison.Ordinal);

            var (boundedExitCode, boundedOutput) = RunWithCapturedOut(
                [
                    leftDb,
                    rightDb,
                    "--json",
                    "--detailed",
                    "--include-content",
                    "--limit",
                    "1",
                    "--max-json-bytes",
                    $"{DiffCommandRunner.MinDiffJsonBytes}",
                ]);
            Assert.Equal(1, boundedExitCode);
            Assert.InRange(
                Encoding.UTF8.GetByteCount(boundedOutput),
                1,
                DiffCommandRunner.MinDiffJsonBytes);
            using var boundedDocument = JsonDocument.Parse(boundedOutput);
            var boundedRoot = boundedDocument.RootElement;
            Assert.Empty(boundedRoot.GetProperty("records").EnumerateArray());
            Assert.Equal("max_json_bytes", boundedRoot.GetProperty("truncation_reason").GetString());
            Assert.True(boundedRoot.GetProperty("first_omitted_record_bytes").GetInt32() > 0);
            Assert.Equal(JsonValueKind.Null, boundedRoot.GetProperty("next_offset").ValueKind);
            Assert.False(boundedRoot.TryGetProperty("next_cursor", out _));
            var replay = boundedRoot.GetProperty("replay");
            Assert.False(replay.TryGetProperty("next_cursor", out _));
            Assert.False(replay.TryGetProperty("next_page_arguments", out _));
            Assert.Contains(
                boundedRoot.GetProperty("diagnostics").EnumerateArray(),
                diagnostic => diagnostic.GetProperty("message").GetString()?.Contains(
                    "increase the byte budget",
                    StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_SummaryOnlyDetectsLargeRowDriftAfterSharedDisplayPrefix_Issue3163()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_large_prefix_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_large_prefix_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");

            var sharedPrefix = new string('x', LargeDiffFieldLength);
            UpdateFirstChunkContent(leftDb, sharedPrefix + "left");
            UpdateFirstChunkContent(rightDb, sharedPrefix + "right");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only", "--data-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.False(document.RootElement.GetProperty("identical").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonUsesSqlOrderForStreamingSymbolDiff_Issue2885()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_order_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_order_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/aa.cs", "csharp", "public class aa { }");
            TestProjectHelper.InsertIndexedFile(leftDb, "src/b.cs", "csharp", "public class b { }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/b.cs", "csharp", "public class b { }");

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--include-content", "--limit", "5"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var leftRecords = GetRecords(root, "symbol", "left");
            var rightRecords = GetRecords(root, "symbol", "right");
            Assert.Contains(leftRecords, record => GetField(record, "path").GetProperty("value").GetString() == "src/aa.cs");
            Assert.DoesNotContain(leftRecords, record => GetField(record, "path").GetProperty("value").GetString() == "src/b.cs");
            Assert.Empty(rightRecords);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonHandlesLegacySymbolRowsWithoutMetadataTargetSource_Issue3524()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_metadata_source_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_metadata_source_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { }");
            RecreateSymbolsTableWithoutMetadataTargetSourceColumn(leftDb);

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--json", "--detailed", "--data-only", "--limit", "1"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("identical", document.RootElement.GetProperty("status").GetString());
            Assert.True(document.RootElement.GetProperty("identical").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_ReturnsSuccessForSeparatelyBuiltIdenticalDatabases_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_identical_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_identical_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: false);

            var (exitCode, output) = RunWithCapturedOut(
                [leftDb, rightDb, "--summary-only", "--data-only"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("identical", document.RootElement.GetProperty("status").GetString());
            Assert.True(document.RootElement.GetProperty("identical").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_ReturnsSchemaMismatchExitCodeBeforeDriftExitCode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_schema_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_schema_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: false);
            SetUserVersion(rightDb, 999);

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(output);
            var summary = document.RootElement.GetProperty("summary");
            Assert.False(summary.GetProperty("schema_versions_equal").GetBoolean());
            Assert.Contains(
                "schema:schema_version_changed",
                summary.GetProperty("difference_reasons").EnumerateArray().Select(item => item.GetString()));
            Assert.False(GetCategory(document.RootElement, "data").GetProperty("evaluated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_ReturnsSchemaMismatchForLegacyDatabaseBeforeReadingMissingTables_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_legacy_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_legacy_right");
        try
        {
            var legacyDb = CreateLegacyDbWithoutGraphTables(leftRoot);
            var currentDb = SeedDb(rightRoot, includeExtraFile: false);

            var (exitCode, output) = RunWithCapturedOut([legacyDb, currentDb, "--summary-only"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("schema_mismatch", document.RootElement.GetProperty("status").GetString());
            Assert.False(document.RootElement.GetProperty("summary").GetProperty("schema_versions_equal").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountSymbolDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_symbol_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_symbol_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class LeftOnly { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class RightOnly { public void Run() { } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountReferenceDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_ref_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_ref_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { Foo(); } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { Bar(); } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("reference_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountSignatureDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_signature_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_signature_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public string Convert(int value) => value.ToString(); }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public string Convert(int value) => value.ToString(); }");
            InsertSyntheticMethodSymbol(leftDb, "src/Same.cs", "Convert", "public string Convert(int value)");
            InsertSyntheticMethodSymbol(rightDb, "src/Same.cs", "Convert", "public string Convert(long value)");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountFoldedSymbolDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_fold_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_fold_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            UpdateFirstSymbolFoldedName(rightDb, "drifted");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountChunkDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_chunk_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_chunk_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            UpdateFirstChunkContent(rightDb, "public class Same { public void Drifted() { } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("reference_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountReferenceLineDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_refline_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_refline_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { Foo(); } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { Foo(); } }");
            UpdateFirstReferenceLineContext(rightDb, "public class Same { public void Run() { Drifted(); } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("reference_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonIgnoresReferenceLineSurrogateIds_Issue4478()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_refline_id_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_refline_id_right");
        try
        {
            var content = "public class Same { public void Run() { Foo(); } }";
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", content);
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", content);
            SetMeta(leftDb, DbContext.IndexedProjectRootMetaKey, Path.GetFullPath(leftRoot));
            SetMeta(rightDb, DbContext.IndexedProjectRootMetaKey, Path.GetFullPath(leftRoot));
            RemapReferenceLineIds(rightDb);

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--json", "--detailed"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("identical", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("identical").GetBoolean());
            Assert.Empty(root.GetProperty("records").EnumerateArray());
            Assert.Equal(0, root.GetProperty("total_count").GetInt64());
            Assert.Equal(0, root.GetProperty("returned_count").GetInt32());
            Assert.Equal(0, root.GetProperty("omitted_count").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_ReturnsUnreadableExitCodeForMissingDatabase_Issue1724()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_diff_missing");
        try
        {
            var db = SeedDb(root, includeExtraFile: false);
            var missing = Path.Combine(root, "missing.db");

            var (exitCode, output) = RunWithCapturedOut([db, missing, "--summary-only"]);

            Assert.Equal(3, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    private static string SeedDb(string projectRoot, bool includeExtraFile)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        for (var i = 0; i < 50; i++)
        {
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/File{i:00}.cs",
                "csharp",
                $$"""
                public static class File{{i:00}}
                {
                    public static void Run() { }
                }
                """);
        }

        if (includeExtraFile)
        {
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Extra.cs",
                "csharp",
                """
                public static class Extra
                {
                    public static void Run() { }
                }
                """);
        }

        return dbPath;
    }

    private static string CreateLegacyDbWithoutGraphTables(string projectRoot)
    {
        var dbPath = Path.Combine(projectRoot, ".cdidx", "legacy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 1;
            CREATE TABLE files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                lang TEXT,
                size INTEGER,
                lines INTEGER,
                checksum TEXT,
                modified DATETIME,
                indexed_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO files (path, lang, size, lines, checksum, modified)
            VALUES ('src/Legacy.cs', 'csharp', 12, 1, 'legacy', '2026-01-01T00:00:00Z');
            """;
        command.ExecuteNonQuery();
        return dbPath;
    }

    private static void SetUserVersion(string dbPath, int version)
    {
        ExecuteNonQuery(dbPath, $"PRAGMA user_version = {version}");
    }

    private static void SetMeta(string dbPath, string key, string? value)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(key, value);
    }

    private static void InsertSyntheticMethodSymbol(string dbPath, string path, string name, string signature)
    {
        ExecuteNonQuery(
            dbPath,
            """
            INSERT INTO symbols (
                file_id,
                kind,
                sub_kind,
                name,
                line,
                start_line,
                start_column,
                end_line,
                body_start_line,
                body_end_line,
                signature,
                container_kind,
                container_name,
                container_qualified_name,
                family_key,
                visibility,
                return_type,
                is_metadata_target
            )
            VALUES (
                (
                SELECT id
                FROM files
                WHERE path = $path
                LIMIT 1
                ),
                'method',
                'method',
                $name,
                1,
                1,
                42,
                1,
                1,
                1,
                $signature,
                'class',
                'Same',
                'Same',
                'Same.Convert',
                'public',
                'string',
                0
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$signature", signature);
            });
    }

    private static void UpdateFirstChunkContent(string dbPath, string content)
    {
        ExecuteNonQuery(
            dbPath,
            """
            UPDATE chunks
            SET content = $content
            WHERE id = (
                SELECT id
                FROM chunks
                ORDER BY id
                LIMIT 1
            )
            """,
            command => command.Parameters.AddWithValue("$content", content));
    }

    private static void UpdateFirstSymbolFoldedName(string dbPath, string nameFolded)
    {
        ExecuteNonQuery(
            dbPath,
            """
            UPDATE symbols
            SET name_folded = $nameFolded
            WHERE id = (
                SELECT id
                FROM symbols
                ORDER BY id
                LIMIT 1
            )
            """,
            command => command.Parameters.AddWithValue("$nameFolded", nameFolded));
    }

    private static void UpdateFirstReferenceLineContext(string dbPath, string context)
    {
        ExecuteNonQuery(
            dbPath,
            """
            UPDATE reference_lines
            SET context = $context
            WHERE id = (
                SELECT id
                FROM reference_lines
                ORDER BY id
                LIMIT 1
            )
            """,
            command => command.Parameters.AddWithValue("$context", context));
    }

    private static void RemapReferenceLineIds(string dbPath)
    {
        ExecuteNonQuery(
            dbPath,
            """
            PRAGMA foreign_keys = OFF;
            UPDATE reference_lines
            SET id = id + 1000000;
            UPDATE symbol_references
            SET reference_line_id = reference_line_id + 1000000
            WHERE reference_line_id IS NOT NULL;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static void RecreateSymbolsTableWithoutMetadataTargetSourceColumn(string dbPath)
    {
        ExecuteNonQuery(
            dbPath,
            """
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
            INSERT INTO symbols (
                id, file_id, kind, sub_kind, name, name_folded, line, start_line,
                start_column, end_line, body_start_line,
                body_end_line, signature, container_kind, container_name,
                container_qualified_name, family_key, visibility, return_type, is_metadata_target
            )
            SELECT
                id, file_id, kind, sub_kind, name, name_folded, line, start_line,
                start_column, end_line, body_start_line,
                body_end_line, signature, container_kind, container_name,
                container_qualified_name, family_key, visibility, return_type, is_metadata_target
            FROM symbols_old;
            DROP TABLE symbols_old;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static void ExecuteNonQuery(string dbPath, string sql, Action<SqliteCommand>? configure = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private static List<JsonElement> GetRecords(JsonElement root, string area, string? side = null)
        => root.GetProperty("records")
            .EnumerateArray()
            .Where(record =>
                record.GetProperty("area").GetString() == area
                && (side is null || record.GetProperty("side").GetString() == side))
            .ToList();

    private static JsonElement GetCategory(JsonElement root, string category)
        => Assert.Single(
            root.GetProperty("summary")
                .GetProperty("categories")
                .EnumerateArray()
                .Where(item => item.GetProperty("category").GetString() == category));

    private static JsonElement GetField(JsonElement record, string name)
        => Assert.Single(
            record.GetProperty("fields")
                .EnumerateArray()
                .Where(field => field.GetProperty("name").GetString() == name));

    private static int GetMaterializedRecordCount(
        List<DiffRecordJsonResult> records,
        int byteBudget,
        CliJsonSerializerContext context)
    {
        long materializedBytes = 0;
        for (var i = 0; i < records.Count; i++)
        {
            materializedBytes += JsonSerializer.SerializeToUtf8Bytes(
                records[i],
                context.DiffRecordJsonResult).LongLength;
            if (materializedBytes > byteBudget)
                return i + 1;
        }

        return records.Count;
    }

    private (int ExitCode, string Output) RunWithCapturedOut(string[] args, CancellationToken cancellationToken = default)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                Console.SetOut(writer);
                var exitCode = DiffCommandRunner.Run(args, _jsonOptions, cancellationToken);
                return (exitCode, writer.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunWithCapturedStreams(string[] args, CancellationToken cancellationToken = default)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = DiffCommandRunner.Run(args, _jsonOptions, cancellationToken);
                return (exitCode, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }
}
