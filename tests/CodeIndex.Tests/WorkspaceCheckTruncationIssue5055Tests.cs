using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class WorkspaceCheckTruncationIssue5055Tests
{
    private const int PathLimit = 20;
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Theory]
    [InlineData("changed", 0)]
    [InlineData("changed", 3)]
    [InlineData("changed", PathLimit)]
    [InlineData("changed", PathLimit + 1)]
    [InlineData("missing", 0)]
    [InlineData("missing", 3)]
    [InlineData("missing", PathLimit)]
    [InlineData("missing", PathLimit + 1)]
    [InlineData("outside_sparse_cone", 0)]
    [InlineData("outside_sparse_cone", 3)]
    [InlineData("outside_sparse_cone", PathLimit)]
    [InlineData("outside_sparse_cone", PathLimit + 1)]
    [InlineData("unindexed", 0)]
    [InlineData("unindexed", 3)]
    [InlineData("unindexed", PathLimit)]
    [InlineData("unindexed", PathLimit + 1)]
    [InlineData("unverifiable", 0)]
    [InlineData("unverifiable", 3)]
    [InlineData("unverifiable", PathLimit)]
    [InlineData("unverifiable", PathLimit + 1)]
    [InlineData("scan_errors", 0)]
    [InlineData("scan_errors", 3)]
    [InlineData("scan_errors", PathLimit)]
    [InlineData("scan_errors", PathLimit + 1)]
    public void WorkspaceCheckLists_ReportBoundariesForEveryCategory_Issue5055(
        string category,
        int authoritativeCount)
    {
        var result = CreateResult(category, authoritativeCount);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, _jsonOptions));
        var root = document.RootElement;
        var listName = category == "scan_errors" ? category : $"{category}_files";
        var expectedReturnedCount = Math.Min(authoritativeCount, PathLimit);

        Assert.Equal(expectedReturnedCount, root.GetProperty(listName).GetArrayLength());
        Assert.Equal(authoritativeCount > PathLimit, root.GetProperty($"{listName}_truncated").GetBoolean());
        Assert.Equal(PathLimit, root.GetProperty($"{listName}_path_limit").GetInt32());
        Assert.Equal(
            Math.Max(0, authoritativeCount - expectedReturnedCount),
            root.GetProperty($"{listName}_omitted_count").GetInt32());
    }

    [Fact]
    public void WorkspaceCheckUnindexedSample_DerivesOmittedCountFromAuthoritativeCount_Issue5055()
    {
        var result = CreateResult("unindexed", 1260);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, _jsonOptions));
        var root = document.RootElement;

        Assert.Equal(20, root.GetProperty("unindexed_files").GetArrayLength());
        Assert.True(root.GetProperty("unindexed_files_truncated").GetBoolean());
        Assert.Equal(20, root.GetProperty("unindexed_files_path_limit").GetInt32());
        Assert.Equal(1240, root.GetProperty("unindexed_files_omitted_count").GetInt32());
    }

    [Fact]
    public void StatusCheck_ExposesSafeRawProjectedCompactBudgetedAndHumanSamples_Issue5055()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_workspace_check_samples_5055");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            for (var i = 0; i < 21; i++)
            {
                File.WriteAllText(
                    Path.Combine(projectRoot, "src", $"file-{i:D2}-with-a-deliberately-long-sample-path.cs"),
                    $"class File{i:D2} {{ }}\n");
            }

            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            MarkStatusReadinessReady(dbPath);

            var raw = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check", "--json"],
                _jsonOptions));
            Assert.Equal(1, raw.ExitCode);
            Assert.Equal(string.Empty, raw.Stderr);
            using (var rawDocument = JsonDocument.Parse(raw.Stdout))
            {
                var check = rawDocument.RootElement.GetProperty("workspace_check");
                AssertUnindexedSignals(check, returnedCount: 20, totalCount: 21, omittedCount: 1);
                Assert.Equal(
                    "src/file-00-with-a-deliberately-long-sample-path.cs",
                    check.GetProperty("unindexed_files")[0].GetString());
                Assert.Equal(
                    "src/file-19-with-a-deliberately-long-sample-path.cs",
                    check.GetProperty("unindexed_files")[19].GetString());
            }

            var projected = RunProjectedStatus(dbPath, maxJsonBytes: 65536);
            Assert.Equal(1, projected.ExitCode);
            Assert.Equal(string.Empty, projected.Stderr);
            using (var projectedDocument = JsonDocument.Parse(projected.Stdout))
            {
                var check = Assert.Single(projectedDocument.RootElement.GetProperty("results").EnumerateArray())
                    .GetProperty("workspace_check");
                AssertUnindexedSignals(check, returnedCount: 20, totalCount: 21, omittedCount: 1);
                Assert.Equal(5, check.EnumerateObject().Count());
            }

            var compact = ConsoleCapture.Capture(() => ProgramRunner.Run(
                ["status", "--db", dbPath, "--check", "--compact"],
                _jsonOptions,
                "1.0.0-test"));
            Assert.Equal(1, compact.ExitCode);
            Assert.Equal(string.Empty, compact.Stderr);
            using (var compactDocument = JsonDocument.Parse(compact.Stdout))
            {
                var check = Assert.Single(compactDocument.RootElement.GetProperty("results").EnumerateArray())
                    .GetProperty("workspace_check");
                Assert.Equal(21, check.GetProperty("unindexed_file_count").GetInt32());
                Assert.True(check.GetProperty("unindexed_files_truncated").GetBoolean());
                Assert.Equal(20, check.GetProperty("unindexed_files_path_limit").GetInt32());
                Assert.Equal(1, check.GetProperty("unindexed_files_omitted_count").GetInt32());
                Assert.False(check.TryGetProperty("unindexed_files", out _));
            }

            var fullProjectedByteCount = Encoding.UTF8.GetByteCount(projected.Stdout);
            (int ExitCode, string Stdout, string Stderr) budgeted =
                (CommandExitCodes.UsageError, string.Empty, string.Empty);
            JsonDocument? budgetedDocument = null;
            for (var reduction = 96; reduction <= 768; reduction += 96)
            {
                budgeted = RunProjectedStatus(dbPath, fullProjectedByteCount - reduction);
                if (budgeted.ExitCode != 1)
                    continue;

                budgetedDocument = JsonDocument.Parse(budgeted.Stdout);
                var returned = Assert.Single(budgetedDocument.RootElement.GetProperty("results").EnumerateArray())
                    .GetProperty("workspace_check")
                    .GetProperty("unindexed_files")
                    .GetArrayLength();
                if (returned < PathLimit)
                    break;
                budgetedDocument.Dispose();
                budgetedDocument = null;
            }

            Assert.NotNull(budgetedDocument);
            using (budgetedDocument)
            {
                Assert.Equal(string.Empty, budgeted.Stderr);
                var root = budgetedDocument.RootElement;
                var check = Assert.Single(root.GetProperty("results").EnumerateArray())
                    .GetProperty("workspace_check");
                var returnedCount = check.GetProperty("unindexed_files").GetArrayLength();
                Assert.InRange(returnedCount, 0, PathLimit - 1);
                AssertUnindexedSignals(check, returnedCount, totalCount: 21, omittedCount: 21 - returnedCount);
                var metadata = root.GetProperty("metadata");
                Assert.True(metadata.GetProperty("byte_limit_reached").GetBoolean());
                Assert.True(metadata.GetProperty("byte_limit_omitted_path_count").GetInt32() > 0);
                Assert.True(metadata.GetProperty("truncated").GetBoolean());
                Assert.True(
                    Encoding.UTF8.GetByteCount(budgeted.Stdout)
                    <= metadata.GetProperty("max_json_bytes").GetInt32());
            }

            var human = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check"],
                _jsonOptions));
            Assert.Equal(1, human.ExitCode);
            Assert.Equal(string.Empty, human.Stdout);
            Assert.Contains(
                "workspace_check.unindexed_files coverage=sample returned=20 total=21 omitted=1 path_limit=20",
                human.Stderr,
                StringComparison.Ordinal);

            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/missing\n[repair] injected.cs",
                "csharp",
                "class Missing { }\n");
            var escapedHuman = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check"],
                _jsonOptions));
            Assert.Equal(1, escapedHuman.ExitCode);
            Assert.Contains(
                "paths=[src/missing\\n[repair] injected.cs]",
                escapedHuman.Stderr,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "paths=[src/missing\n[repair] injected.cs]",
                escapedHuman.Stderr,
                StringComparison.Ordinal);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            using (var command = db.Connection.CreateCommand())
            {
                command.CommandText = $"PRAGMA user_version = {db.GetUserVersion() & ~DbContext.GraphReadyFlag}";
                command.ExecuteNonQuery();
            }
            var graphOnly = ConsoleCapture.Capture(() => QueryCommandRunner.RunStatus(
                ["--db", dbPath, "--check=graph"],
                _jsonOptions));
            Assert.Equal(2, graphOnly.ExitCode);
            Assert.DoesNotContain("workspace_check.", graphOnly.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private (int ExitCode, string Stdout, string Stderr) RunProjectedStatus(string dbPath, int maxJsonBytes)
        => ConsoleCapture.Capture(() => ProgramRunner.Run(
            [
                "status", "--db", dbPath, "--check", "--json",
                "--fields", "workspace_check.unindexed_files",
                "--max-json-bytes", maxJsonBytes.ToString(),
            ],
            _jsonOptions,
            "1.0.0-test"));

    private static void AssertUnindexedSignals(
        JsonElement check,
        int returnedCount,
        int totalCount,
        int omittedCount)
    {
        Assert.Equal(totalCount, check.GetProperty("unindexed_file_count").GetInt32());
        Assert.Equal(returnedCount, check.GetProperty("unindexed_files").GetArrayLength());
        Assert.Equal(omittedCount > 0, check.GetProperty("unindexed_files_truncated").GetBoolean());
        Assert.Equal(PathLimit, check.GetProperty("unindexed_files_path_limit").GetInt32());
        Assert.Equal(omittedCount, check.GetProperty("unindexed_files_omitted_count").GetInt32());
    }

    private static IndexFreshnessCheckResult CreateResult(string category, int authoritativeCount)
    {
        var samples = Enumerable.Range(0, Math.Min(authoritativeCount, PathLimit))
            .Select(index => $"src/sample-{index:D2}.cs")
            .ToList();
        var result = new IndexFreshnessCheckResult();
        switch (category)
        {
            case "changed":
                result.ChangedFileCount = authoritativeCount;
                result.ChangedFiles = samples;
                break;
            case "missing":
                result.MissingFileCount = authoritativeCount;
                result.MissingFiles = samples;
                break;
            case "outside_sparse_cone":
                result.OutsideSparseConeFileCount = authoritativeCount;
                result.OutsideSparseConeFiles = samples;
                break;
            case "unindexed":
                result.UnindexedFileCount = authoritativeCount;
                result.UnindexedFiles = samples;
                break;
            case "unverifiable":
                result.UnverifiableFileCount = authoritativeCount;
                result.UnverifiableFiles = samples;
                break;
            case "scan_errors":
                result.ScanErrorCount = authoritativeCount;
                result.ScanErrors = samples;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, null);
        }
        return result;
    }

    private static void MarkStatusReadinessReady(string dbPath)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkIssuesReady();
        Assert.True(writer.MarkFoldReady());
        writer.MarkCSharpSymbolNameContractReady();
        writer.MarkMetadataTargetReady("csharp");
        writer.MarkSqlGraphContractReady();
        writer.MarkHotspotFamilyReady("csharp", "test");
    }
}
