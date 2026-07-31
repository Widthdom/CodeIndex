using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunValidate_ReplacementCharJson_IncludesOriginAndSeverity()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_validate_replacement_origin");
        var projectRoot = project.Root;
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.WriteTextFile(
            projectRoot,
            "src/literal.cs",
            "class Literal { const char Value = '\uFFFD'; }\n");

        var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
            [projectRoot, "--db", dbPath, "--json", "--quiet"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, indexExitCode);
        Assert.Equal(string.Empty, indexStderr);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--json", "--kind", "replacement_char"],
            _jsonOptions));

        using var document = ParseJsonOutput(stdout);
        var json = document.RootElement;
        var issue = json.GetProperty("issues")[0];

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.Equal("replacement_char", issue.GetProperty("kind").GetString());
        Assert.Equal(FileIssue.OriginSourceLiteral, issue.GetProperty("origin").GetString());
        Assert.Equal(FileIssue.SeverityInfo, issue.GetProperty("severity").GetString());
        Assert.Equal(FileIssue.CategoryIntentionalSourceLiteral, issue.GetProperty("category").GetString());
        Assert.False(issue.GetProperty("actionable").GetBoolean());
    }

    [Fact]
    public void RunValidate_HumanOutputLabelsSourceLiteralTestFixtures_Issue4068()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_validate_fixture_label_4068");
        var projectRoot = project.Root;
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.WriteTextFiles(
            projectRoot,
            new Dictionary<string, string>
            {
                ["tests/fixtures/literal.cs"] = "class Literal { const char Value = '\uFFFD'; }\n",
                ["src/foo.test.cs"] = "class FooTest { const char Value = '\uFFFD'; }\n",
                ["tests_utils/helper.cs"] = "class Helper { const char Value = '\uFFFD'; }\n",
                ["conftest.py"] = "VALUE = '\uFFFD'\n",
            });

        var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
            [projectRoot, "--db", dbPath, "--json", "--quiet"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, indexExitCode);
        Assert.Equal(string.Empty, indexStderr);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--kind", "replacement_char"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Contains("replacement_char", stdout);
        Assert.Contains("conftest.py", stdout);
        Assert.Contains("src/foo.test.cs", stdout);
        Assert.Contains("tests/fixtures/literal.cs", stdout);
        Assert.Contains("tests_utils/helper.cs", stdout);
        Assert.Contains("[info, source_literal, test_fixture]", stdout);
        Assert.Contains("U+FFFD source literal", stdout);
        Assert.Contains("(4 issues: replacement_char: 4)", stderr);
        Assert.Contains("Summary: actionable: 0", stderr);
        Assert.Contains("category: expected_fixture_literal: 4", stderr);
    }

    [Fact]
    public void RunValidate_CrossFormatMetadataPreservesTotalsActionabilityAndSeverity_Issues4138_4583_And4908()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_validate_summary_4138");
        var projectRoot = project.Root;
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.WriteTextFile(
            projectRoot,
            "tests/fixtures/literal.cs",
            "class Literal { const char Value = '\uFFFD'; }\n");

        var decodeBytes = new List<byte>();
        decodeBytes.AddRange(System.Text.Encoding.UTF8.GetBytes("class Decode { const string Value = \""));
        decodeBytes.Add(0xFF);
        decodeBytes.AddRange(System.Text.Encoding.UTF8.GetBytes("\"; }\n"));
        TestProjectHelper.WriteBinaryFile(projectRoot, "src/decode.cs", decodeBytes.ToArray());

        var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
            [projectRoot, "--db", dbPath, "--json", "--quiet"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, indexExitCode);
        Assert.Equal(string.Empty, indexStderr);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--json", "--kind", "replacement_char"],
            _jsonOptions));
        var (arrayExitCode, arrayStdout, arrayStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--json=array", "--kind", "replacement_char"],
            _jsonOptions));
        var (compactExitCode, compactStdout, compactStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "compact", "--kind", "replacement_char"],
            _jsonOptions));
        var (limitedJsonExitCode, limitedJsonStdout, limitedJsonStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--json", "--kind", "replacement_char", "--limit", "1"],
            _jsonOptions));
        var (limitedCompactExitCode, limitedCompactStdout, limitedCompactStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "compact", "--kind", "replacement_char", "--limit", "1"],
            _jsonOptions));
        var (sarifExitCode, sarifStdout, sarifStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "sarif", "--kind", "replacement_char"],
            _jsonOptions));
        var (limitedSarifExitCode, limitedSarifStdout, limitedSarifStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "sarif", "--kind", "replacement_char", "--limit", "1"],
            _jsonOptions));
        var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "count", "--kind", "replacement_char", "--limit", "1"],
            _jsonOptions));
        var (emptyCountExitCode, emptyCountStdout, emptyCountStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "count", "--kind", "replacement_char", "--severity", "error"],
            _jsonOptions));
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            new DbWriter(db).MarkIndexIncomplete(["test_incomplete"]);
        }
        var (incompleteCountExitCode, incompleteCountStdout, incompleteCountStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "count", "--kind", "replacement_char"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(CommandExitCodes.Success, arrayExitCode);
        Assert.Equal(CommandExitCodes.Success, compactExitCode);
        Assert.Equal(CommandExitCodes.Success, limitedJsonExitCode);
        Assert.Equal(CommandExitCodes.Success, limitedCompactExitCode);
        Assert.Equal(CommandExitCodes.Success, sarifExitCode);
        Assert.Equal(CommandExitCodes.Success, limitedSarifExitCode);
        Assert.Equal(CommandExitCodes.Success, countExitCode);
        Assert.Equal(CommandExitCodes.Success, emptyCountExitCode);
        Assert.Equal(CommandExitCodes.Success, incompleteCountExitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(string.Empty, arrayStderr);
        Assert.Equal(string.Empty, compactStderr);
        Assert.Equal(string.Empty, limitedJsonStderr);
        Assert.Equal(string.Empty, limitedCompactStderr);
        Assert.Equal(string.Empty, sarifStderr);
        Assert.Equal(string.Empty, limitedSarifStderr);
        Assert.Equal(string.Empty, countStderr);
        Assert.Equal(string.Empty, emptyCountStderr);
        Assert.Equal(string.Empty, incompleteCountStderr);

        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;
        var summary = root.GetProperty("summary");
        Assert.Equal(2, root.GetProperty("returned").GetInt32());
        Assert.Equal(2, root.GetProperty("total").GetInt32());
        Assert.Equal(0, root.GetProperty("omitted").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, summary.GetProperty("total").GetInt32());
        Assert.Equal(1, summary.GetProperty("actionable").GetInt32());
        Assert.Equal(1, summary.GetProperty("informational").GetInt32());
        Assert.Equal("mixed", summary.GetProperty("actionability").GetString());
        Assert.Equal(1, summary.GetProperty("by_category").GetProperty(FileIssue.CategoryExpectedFixtureLiteral).GetInt32());
        Assert.Equal(1, summary.GetProperty("by_category").GetProperty(FileIssue.CategoryDecodingRisk).GetInt32());

        var issues = root.GetProperty("issues").EnumerateArray().ToList();
        Assert.Contains(issues, issue =>
            issue.GetProperty("category").GetString() == FileIssue.CategoryExpectedFixtureLiteral
            && !issue.GetProperty("actionable").GetBoolean());
        Assert.Contains(issues, issue =>
            issue.GetProperty("category").GetString() == FileIssue.CategoryDecodingRisk
            && issue.GetProperty("actionable").GetBoolean());

        using var arrayDocument = JsonDocument.Parse(arrayStdout);
        Assert.All(arrayDocument.RootElement.EnumerateArray(), issue =>
        {
            Assert.True(issue.TryGetProperty("category", out _));
            Assert.True(issue.TryGetProperty("actionable", out _));
        });

        using var compactDocument = ParseJsonOutput(compactStdout);
        Assert.Equal("compact", compactDocument.RootElement.GetProperty("format").GetString());
        Assert.Equal("mixed", compactDocument.RootElement.GetProperty("summary").GetProperty("actionability").GetString());
        Assert.Equal(2, compactDocument.RootElement.GetProperty("issues").GetArrayLength());

        using var limitedJsonDocument = ParseJsonOutput(limitedJsonStdout);
        AssertValidatePageMetadata(limitedJsonDocument.RootElement, returned: 1, total: 2, omitted: 1, truncated: true);
        Assert.Equal(2, limitedJsonDocument.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(1, limitedJsonDocument.RootElement.GetProperty("issues").GetArrayLength());

        using var limitedCompactDocument = ParseJsonOutput(limitedCompactStdout);
        AssertValidatePageMetadata(limitedCompactDocument.RootElement, returned: 1, total: 2, omitted: 1, truncated: true);
        Assert.Equal(2, limitedCompactDocument.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Equal(1, limitedCompactDocument.RootElement.GetProperty("issues").GetArrayLength());

        using var sarifDocument = ParseJsonOutput(sarifStdout);
        var sarifResults = sarifDocument.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(sarifResults, result =>
            result.GetProperty("level").GetString() == "note"
            && result.GetProperty("properties").GetProperty("severity").GetString() == FileIssue.SeverityInfo
            && !result.GetProperty("properties").GetProperty("actionable").GetBoolean());
        Assert.Contains(sarifResults, result =>
            result.GetProperty("level").GetString() == "warning"
            && result.GetProperty("properties").GetProperty("severity").GetString() == FileIssue.SeverityWarning
            && result.GetProperty("properties").GetProperty("actionable").GetBoolean());

        using var limitedSarifDocument = ParseJsonOutput(limitedSarifStdout);
        var limitedSarifRun = limitedSarifDocument.RootElement.GetProperty("runs")[0];
        var limitedSarifProperties = limitedSarifRun.GetProperty("properties");
        AssertValidatePageMetadata(limitedSarifProperties, returned: 1, total: 2, omitted: 1, truncated: true);
        Assert.True(limitedSarifProperties.GetProperty("issues_table_available").GetBoolean());
        Assert.False(limitedSarifProperties.GetProperty("degraded").GetBoolean());
        Assert.Equal(1, limitedSarifRun.GetProperty("results").GetArrayLength());

        using var countDocument = ParseJsonOutput(countStdout);
        var countRoot = countDocument.RootElement;
        Assert.Equal(summary.GetProperty("total").GetInt32(), countRoot.GetProperty("count").GetInt32());
        Assert.Equal(2, countRoot.GetProperty("total_estimated").GetInt32());
        Assert.Equal(JsonOutputContract.ApiVersion, countRoot.GetProperty("api_version").GetString());
        Assert.Equal("validation_issues", countRoot.GetProperty("count_kind").GetString());
        Assert.Equal("all_matching_issues_before_limit", countRoot.GetProperty("count_scope").GetString());
        Assert.True(countRoot.GetProperty("issues_table_available").GetBoolean());
        Assert.True(countRoot.GetProperty("file_issues_data_current").GetBoolean());
        Assert.True(countRoot.GetProperty("index_complete").GetBoolean());
        Assert.True(countRoot.GetProperty("freshness_available").GetBoolean());
        Assert.False(countRoot.GetProperty("degraded").GetBoolean());
        Assert.True(countRoot.GetProperty("authoritative_count").GetBoolean());
        Assert.True(countRoot.GetProperty("query_context").GetProperty("count").GetBoolean());
        Assert.Equal("replacement_char", countRoot.GetProperty("query_context").GetProperty("kind").GetString());
        Assert.Equal(1, countRoot.GetProperty("query_context").GetProperty("limit").GetInt32());
        Assert.False(countRoot.TryGetProperty("issues", out _));

        using var emptyCountDocument = ParseJsonOutput(emptyCountStdout);
        var emptyCountRoot = emptyCountDocument.RootElement;
        Assert.Equal(0, emptyCountRoot.GetProperty("count").GetInt32());
        Assert.Equal(0, emptyCountRoot.GetProperty("total_estimated").GetInt32());
        Assert.Equal("error", emptyCountRoot.GetProperty("query_context").GetProperty("severity").GetString());
        Assert.True(emptyCountRoot.GetProperty("authoritative_count").GetBoolean());

        using var incompleteCountDocument = ParseJsonOutput(incompleteCountStdout);
        var incompleteCountRoot = incompleteCountDocument.RootElement;
        Assert.Equal(2, incompleteCountRoot.GetProperty("count").GetInt32());
        Assert.True(incompleteCountRoot.GetProperty("issues_table_available").GetBoolean());
        Assert.True(incompleteCountRoot.GetProperty("file_issues_data_current").GetBoolean());
        Assert.False(incompleteCountRoot.GetProperty("index_complete").GetBoolean());
        Assert.Contains(
            incompleteCountRoot.GetProperty("index_incomplete_reasons").EnumerateArray(),
            reason => reason.GetString() == "test_incomplete");
        Assert.True(incompleteCountRoot.GetProperty("degraded").GetBoolean());
        Assert.False(incompleteCountRoot.GetProperty("authoritative_count").GetBoolean());
    }

    [Fact]
    public void RunValidate_EmptySarifAndCountReportUnavailableIssueDataAsDegraded_Issues4583And4908()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_validate_sarif_degraded_4583");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE file_issues";
            command.ExecuteNonQuery();
        }

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "sarif"],
            _jsonOptions));
        var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "count"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(CommandExitCodes.Success, countExitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(string.Empty, countStderr);
        using var document = ParseJsonOutput(stdout);
        var run = document.RootElement.GetProperty("runs")[0];
        var properties = run.GetProperty("properties");
        AssertValidatePageMetadata(properties, returned: 0, total: 0, omitted: 0, truncated: false);
        Assert.False(properties.GetProperty("issues_table_available").GetBoolean());
        Assert.True(properties.GetProperty("degraded").GetBoolean());
        Assert.Empty(run.GetProperty("results").EnumerateArray());

        using var countDocument = ParseJsonOutput(countStdout);
        var countRoot = countDocument.RootElement;
        Assert.Equal(0, countRoot.GetProperty("count").GetInt32());
        Assert.Equal(0, countRoot.GetProperty("total_estimated").GetInt32());
        Assert.False(countRoot.GetProperty("issues_table_available").GetBoolean());
        Assert.False(countRoot.GetProperty("file_issues_data_current").GetBoolean());
        Assert.True(countRoot.GetProperty("index_complete").GetBoolean());
        Assert.False(countRoot.TryGetProperty("index_incomplete_reasons", out _));
        Assert.True(countRoot.GetProperty("degraded").GetBoolean());
        Assert.False(countRoot.GetProperty("authoritative_count").GetBoolean());
    }

    private static void AssertValidatePageMetadata(
        JsonElement payload,
        int returned,
        int total,
        int omitted,
        bool truncated)
    {
        Assert.Equal(returned, payload.GetProperty("returned").GetInt32());
        Assert.Equal(total, payload.GetProperty("total").GetInt32());
        Assert.Equal(omitted, payload.GetProperty("omitted").GetInt32());
        Assert.Equal(truncated, payload.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void RunValidate_FormatLspIncludesRangeAndDiagnosticMetadata_Issue3949()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_validate_lsp_metadata_3949");
        var projectRoot = project.Root;
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "CodeIndex.sln",
            "solution",
            "Microsoft Visual Studio Solution File\n");

        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            db.InitializeSchema();
            var writer = new DbWriter(db.Connection);
            using var fileCmd = db.Connection.CreateCommand();
            fileCmd.CommandText = "SELECT id FROM files WHERE path = $path";
            fileCmd.Parameters.AddWithValue("$path", "CodeIndex.sln");
            var fileId = (long)fileCmd.ExecuteScalar()!;
            writer.InsertIssues(fileId,
            [
                new FileIssue
                {
                    Path = "CodeIndex.sln",
                    Kind = "solution_header",
                    Line = 0,
                    Message = "Solution header is invalid.",
                    Severity = FileIssue.SeverityWarning,
                }
            ]);
            writer.MarkIssuesReady();
        }

        SqlitePoolCleanup.ClearPoolsForWindowsFileRelease();

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--format", "lsp", "--limit", "1"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var location = Assert.Single(document.RootElement.EnumerateArray());
        var range = location.GetProperty("range");
        Assert.Equal(0, range.GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(0, range.GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(0, range.GetProperty("end").GetProperty("line").GetInt32());
        Assert.Equal(1, range.GetProperty("end").GetProperty("character").GetInt32());
        Assert.Equal("solution_header", location.GetProperty("kind").GetString());
        Assert.Equal("Solution header is invalid.", location.GetProperty("message").GetString());
        Assert.Equal(FileIssue.SeverityWarning, location.GetProperty("severity").GetString());
        Assert.Equal("cdidx validate", location.GetProperty("source").GetString());
    }

    [Fact]
    public void RunValidate_SeverityFilterNarrowsIssues_Issue3008()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_validate_severity_filter");
        var projectRoot = project.Root;
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        TestProjectHelper.WriteTextFile(
            projectRoot,
            "src/literal.cs",
            "class Literal { const char Value = '\uFFFD'; }\n");

        var bytes = new List<byte>();
        void AddUtf8(string text) => bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(text));
        AddUtf8("line1 clean\n");
        AddUtf8("line2 has ");
        bytes.Add(0xFF);
        AddUtf8(" here\n");
        for (var i = 0; i < 200; i++)
            AddUtf8("filler ascii ascii ascii\n");
        TestProjectHelper.WriteBinaryFile(projectRoot, "src/decode.cs", bytes.ToArray());

        var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
            [projectRoot, "--db", dbPath, "--json", "--quiet"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, indexExitCode);
        Assert.Equal(string.Empty, indexStderr);

        var (warningExitCode, warningStdout, warningStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--json", "--kind", "replacement_char", "--severity", "warning"],
            _jsonOptions));
        var (infoExitCode, infoStdout, infoStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--db", dbPath, "--json", "--kind", "replacement_char", "--severity", "info"],
            _jsonOptions));

        using var warningDocument = ParseJsonOutput(warningStdout);
        using var infoDocument = ParseJsonOutput(infoStdout);
        var warningIssues = warningDocument.RootElement.GetProperty("issues");
        var infoIssues = infoDocument.RootElement.GetProperty("issues");

        Assert.Equal(CommandExitCodes.Success, warningExitCode);
        Assert.Equal(CommandExitCodes.Success, infoExitCode);
        Assert.Equal(string.Empty, warningStderr);
        Assert.Equal(string.Empty, infoStderr);
        Assert.True(warningIssues.GetArrayLength() > 0);
        Assert.Equal(warningIssues.GetArrayLength(), warningDocument.RootElement.GetProperty("count").GetInt32());
        Assert.All(warningIssues.EnumerateArray(), issue =>
        {
            Assert.Equal("replacement_char", issue.GetProperty("kind").GetString());
            Assert.Equal(FileIssue.SeverityWarning, issue.GetProperty("severity").GetString());
            Assert.Equal(FileIssue.OriginDecodeReplacement, issue.GetProperty("origin").GetString());
        });
        Assert.Equal(1, infoDocument.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, infoIssues.GetArrayLength());
        Assert.Equal(FileIssue.SeverityInfo, infoIssues[0].GetProperty("severity").GetString());
        Assert.Equal(FileIssue.OriginSourceLiteral, infoIssues[0].GetProperty("origin").GetString());
    }
}
