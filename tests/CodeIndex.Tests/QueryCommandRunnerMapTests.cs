using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunMap_ParseSectionsAndDepth_StoresSelectors()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--json", "--sections", "tree,languages", "--depth", "2"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.True(options.Json);
        Assert.Equal(["tree", "languages"], options.MapSections);
        Assert.True(options.ContextAfterExplicit);
        Assert.Equal(2, options.ContextAfter);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunMap_ParseSectionAliases_StoresCanonicalSelectors_Issue4317()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--json", "--sections", "summary,modules,entrypoints,largest-files"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Equal(["summary", "tree", "hotspots", "metrics"], options.MapSections);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunMap_SectionsListJson_PrintsDiscoverableSectionMetadata_Issue4317()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
            ["--json", "--sections", "list"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        var root = document.RootElement;

        Assert.Contains("tree", root.GetProperty("sections").EnumerateArray().Select(section => section.GetString()));
        Assert.Contains("hotspots", root.GetProperty("sections").EnumerateArray().Select(section => section.GetString()));
        Assert.Equal("hotspots", root.GetProperty("aliases").GetProperty("entrypoints").GetString());
        Assert.True(root.GetProperty("section_properties").TryGetProperty("summary", out _));
    }

    [Fact]
    public void RunMap_DepthReaggregatesScopedModulesByPrefix_Issue4573()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_depth_4573");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/feature4573/App.cs", "csharp", "class App {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/feature4573/Worker.cs", "csharp", "class Worker {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "tests/feature4573/AppTests.cs", "csharp", "class AppTests {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--sections", "tree", "--depth", "1", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();
            Assert.Collection(
                modules.OrderBy(module => module.GetProperty("module").GetString(), StringComparer.Ordinal),
                module =>
                {
                    Assert.Equal("src", module.GetProperty("module").GetString());
                    Assert.Equal(2, module.GetProperty("files").GetInt32());
                },
                module =>
                {
                    Assert.Equal("tests", module.GetProperty("module").GetString());
                    Assert.Equal(1, module.GetProperty("files").GetInt32());
                });
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_ParseSummaryOnly_StoresSelector()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--summary-only"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.True(options.MapSummaryOnly);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunMap_SummaryOnlyAndSections_ReturnsUsageError()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
            ["--summary-only", "--sections", "tree"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("--summary-only cannot be combined with --sections", stderr);
        Assert.Contains("Usage: cdidx map", stderr);
    }

    [Fact]
    public void RunMap_SummaryOnlyJson_OmitsDetailSections_Issue3393()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_summary_only");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "namespace App; public class Program { public static void Main() { } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--summary-only", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("summary_only").GetBoolean());
            Assert.Equal(1, json.GetProperty("file_count").GetInt32());
            Assert.Empty(json.GetProperty("sections").EnumerateArray());
            Assert.False(json.TryGetProperty("languages", out _));
            Assert.False(json.TryGetProperty("modules", out _));
            Assert.False(json.TryGetProperty("top_files", out _));
            Assert.False(json.TryGetProperty("largest_files", out _));
            Assert.False(json.TryGetProperty("symbol_rich_files", out _));
            Assert.False(json.TryGetProperty("reference_rich_files", out _));
            Assert.False(json.TryGetProperty("entrypoints", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_ParseCompact_ImpliesJsonAndPreservesExplicitLimit_Issue3009()
    {
        var options = QueryCommandRunner.ParseArgs(
            ["--compact", "--limit", "3"],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.True(options.Json);
        Assert.True(options.Compact);
        Assert.True(options.LimitExplicit);
        Assert.Equal(3, options.Limit);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void RunMap_FormatCompact_ActsLikeCompactJson_Issue3446()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_format_compact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "namespace Demo; public class App { public void Run() { } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--format", "compact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("compact").GetBoolean());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, json.GetProperty("compact_limit").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_FormatIssueDrafts_EmitsOversizedCandidates_Issue4067()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_issue_drafts");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var largerFile = string.Join('\n', Enumerable.Range(1, QueryCommandRunner.MapIssueDraftLineThreshold + 20).Select(line => $"// large {line}")) + "\n";
            var smallerFile = string.Join('\n', Enumerable.Range(1, QueryCommandRunner.MapIssueDraftLineThreshold + 10).Select(line => $"// smaller {line}")) + "\n";
            var nearThresholdNonCandidate = string.Join('\n', Enumerable.Repeat("x", QueryCommandRunner.MapIssueDraftLineThreshold - 1)) + "\n";
            TestProjectHelper.InsertIndexedFile(dbPath, "src/LargeOne.cs", "csharp", largerFile);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/LargeTwo.cs", "csharp", smallerFile);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/SizeOnly.cs", "csharp", "class SizeOnly {}\n");
            SetIndexedFileSize(dbPath, "src/SizeOnly.cs", QueryCommandRunner.MapIssueDraftByteThreshold + 1);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/NearThreshold.cs", "csharp", nearThresholdNonCandidate);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--format", "issue-drafts", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var draft = Assert.Single(json.GetProperty("issue_drafts").EnumerateArray());
            var candidate = draft.GetProperty("candidate");
            var oversizedGroup = json.GetProperty("groups").GetProperty("oversized_file");
            var candidateTruncation = json.GetProperty("truncation").GetProperty("issue_draft_candidates");
            var legacyTruncationAlias = json.GetProperty("truncation").GetProperty("largest_files");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("issue-drafts", json.GetProperty("format").GetString());
            Assert.Equal(3, json.GetProperty("count").GetInt32());
            Assert.Equal(1, json.GetProperty("emitted_count").GetInt32());
            Assert.Equal(2, json.GetProperty("omitted_count").GetInt32());
            Assert.Equal(2, json.GetProperty("limit_omitted_count").GetInt32());
            Assert.Equal("evaluated_scoped_candidates", json.GetProperty("candidate_source").GetString());
            Assert.Equal("limit", Assert.Single(json.GetProperty("omitted_by").EnumerateArray().ToList()).GetString());
            Assert.Equal(3, oversizedGroup.GetProperty("count").GetInt32());
            Assert.Equal("issue_draft_candidates", oversizedGroup.GetProperty("source_section").GetString());
            Assert.Equal("src/LargeOne.cs", oversizedGroup.GetProperty("representative_paths").EnumerateArray().Single().GetString());
            Assert.True(oversizedGroup.GetProperty("representative_paths_truncated").GetBoolean());
            Assert.Equal("oversized_file", draft.GetProperty("kind").GetString());
            Assert.Contains("src/LargeOne.cs", draft.GetProperty("title").GetString(), StringComparison.Ordinal);
            Assert.Contains("## Checklist", draft.GetProperty("body").GetString(), StringComparison.Ordinal);
            Assert.Equal("src/LargeOne.cs", candidate.GetProperty("path").GetString());
            Assert.Equal("issue_draft_candidates", candidate.GetProperty("source_section").GetString());
            Assert.True(candidate.GetProperty("line_threshold_exceeded").GetBoolean());
            Assert.Equal(QueryCommandRunner.MapIssueDraftLineThreshold, candidate.GetProperty("line_threshold").GetInt32());
            Assert.Equal(QueryCommandRunner.MapIssueDraftByteThreshold, candidate.GetProperty("byte_threshold").GetInt64());
            Assert.Equal(1, candidateTruncation.GetProperty("source_limit").GetInt32());
            Assert.Equal(4, candidateTruncation.GetProperty("total_files").GetInt32());
            Assert.Equal(3, candidateTruncation.GetProperty("total_candidates").GetInt32());
            Assert.True(candidateTruncation.GetProperty("truncated").GetBoolean());
            Assert.Equal("issue_draft_candidates", legacyTruncationAlias.GetProperty("compatibility_alias_for").GetString());

            var (allExitCode, allStdout, allStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--format", "issue-drafts", "--limit", "3"],
                _jsonOptions));
            using var allDocument = ParseJsonOutput(allStdout);
            var allPaths = allDocument.RootElement.GetProperty("issue_drafts").EnumerateArray()
                .Select(item => item.GetProperty("candidate").GetProperty("path").GetString())
                .ToArray();
            Assert.Equal(CommandExitCodes.Success, allExitCode);
            Assert.Equal(string.Empty, allStderr);
            Assert.Contains("src/SizeOnly.cs", allPaths);
            Assert.DoesNotContain("src/NearThreshold.cs", allPaths);
            Assert.False(allDocument.RootElement.GetProperty("truncated").GetBoolean());

            var (summaryExitCode, summaryStdout, summaryStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--format", "issue-drafts", "--limit", "1", "--summary-only"],
                _jsonOptions));
            using var summaryDocument = ParseJsonOutput(summaryStdout);
            var summary = summaryDocument.RootElement;
            Assert.Equal(CommandExitCodes.Success, summaryExitCode);
            Assert.Equal(string.Empty, summaryStderr);
            Assert.Equal(3, summary.GetProperty("count").GetInt32());
            Assert.Equal(0, summary.GetProperty("emitted_count").GetInt32());
            Assert.Equal(3, summary.GetProperty("omitted_count").GetInt32());
            Assert.Equal(3, summary.GetProperty("summary_only_omitted_count").GetInt32());
            Assert.Equal("summary_only", Assert.Single(summary.GetProperty("omitted_by").EnumerateArray().ToList()).GetString());
            Assert.False(summary.GetProperty("truncated").GetBoolean());
            Assert.False(summary.TryGetProperty("row_limit_reached", out _));
            Assert.False(summary.TryGetProperty("limit_omitted_count", out _));
            Assert.True(summary.GetProperty("truncation").GetProperty("issue_draft_candidates").GetProperty("truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_FormatIssueDrafts_AppliesDuplicatePreflight_Issue4425()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_map_duplicate_preflight");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var content = string.Join('\n', Enumerable.Range(1, QueryCommandRunner.MapIssueDraftLineThreshold + 1).Select(line => $"// {line}"));
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Large.cs", "csharp", content);
            var issuesPath = Path.Combine(projectRoot, "issues.json");
            File.WriteAllText(issuesPath, """[{"number":4425,"title":"Split oversized file: src/Large.cs","state":"open","labels":["maintenance"],"html_url":"https://example.invalid/4425"}]""");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--format", "issue-drafts", "--open-issues", issuesPath], _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;
            Assert.True(root.GetProperty("duplicate_preflight").GetProperty("checked").GetBoolean());
            var draft = Assert.Single(root.GetProperty("issue_drafts").EnumerateArray());
            var match = Assert.Single(draft.GetProperty("duplicate_preflight").GetProperty("matches").EnumerateArray());
            Assert.Equal(4425, match.GetProperty("number").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_FormatIssueDraftsSummaryOnly_OmitsRowsButKeepsGroups_Issue4317()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_issue_drafts_summary");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var largerFile = string.Join('\n', Enumerable.Range(1, QueryCommandRunner.MapIssueDraftLineThreshold + 20).Select(line => $"// large {line}")) + "\n";
            TestProjectHelper.InsertIndexedFile(dbPath, "src/LargeOne.cs", "csharp", largerFile);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--format", "issue-drafts", "--summary-only", "--json", "--limit", "10", "--exclude-tests"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;

            Assert.Equal("issue-drafts", root.GetProperty("format").GetString());
            Assert.True(root.GetProperty("summary_only").GetBoolean());
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.Equal(0, root.GetProperty("emitted_count").GetInt32());
            Assert.Equal(1, root.GetProperty("omitted_count").GetInt32());
            Assert.Equal(1, root.GetProperty("summary_only_omitted_count").GetInt32());
            Assert.Empty(root.GetProperty("issue_drafts").EnumerateArray());
            Assert.Equal(1, root.GetProperty("groups").GetProperty("oversized_file").GetProperty("count").GetInt32());
            Assert.Equal("summary_only", Assert.Single(root.GetProperty("omitted_by").EnumerateArray().ToList()).GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_ParseInvalidMinEntrypointConfidence_TruncatesOversizedValue()
    {
        var value = new string('x', ConsoleUi.DefaultDiagnosticValueCharLimit + 1);

        var options = QueryCommandRunner.ParseArgs(
            ["--min-entrypoint-confidence", value],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Contains("--min-entrypoint-confidence must be a number", options.ParseError);
        Assert.Contains("<truncated; original length", options.ParseError);
        Assert.DoesNotContain(value, options.ParseError);
    }

    [Fact]
    public void RunMap_ParseInvalidSections_FlattensControlCharacters_Issue3092()
    {
        var value = "tree,bad\nforged\tvalue";

        var options = QueryCommandRunner.ParseArgs(
            ["--sections", value],
            jsonDefault: false,
            validateDefaultSnippetLines: false,
            validateDefaultMaxLineWidth: false);

        Assert.Contains("--sections contains unsupported section 'bad forged value'", options.ParseError);
        Assert.DoesNotContain("bad\nforged\tvalue", options.ParseError);
    }

    [Fact]
    public void RunMap_CompactJson_CapsSectionsAndReportsTruncation_Issue3009()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_compact");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < QueryCommandRunner.DefaultCompactSectionLimit + 2; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/module{i}/App{i}.cs",
                    "csharp",
                    $"namespace Module{i}; public class App{i} {{ public void Run() {{ }} }}\n");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--compact"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var topFiles = json.GetProperty("top_files").EnumerateArray().ToList();
            var topFilesTruncation = json
                .GetProperty("truncation")
                .GetProperty("sections")
                .GetProperty("top_files");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.True(json.GetProperty("compact").GetBoolean());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, json.GetProperty("compact_limit").GetInt32());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, topFiles.Count);
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit, topFilesTruncation.GetProperty("returned").GetInt32());
            Assert.Equal(QueryCommandRunner.DefaultCompactSectionLimit + 1, topFilesTruncation.GetProperty("source_count").GetInt32());
            Assert.True(topFilesTruncation.GetProperty("truncated").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_CompactJsonHonorsMaxJsonBytesAndReportsNextCommands_Issue4183()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_compact_bytes");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < QueryCommandRunner.DefaultCompactSectionLimit + 2; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/module{i}/App{i}.cs",
                    "csharp",
                    $"namespace Module{i}; public class App{i} {{ public void Run() {{ }} }}\n");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--compact", "--path", "src/**", "--max-json-bytes", "200000"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.True(json.GetProperty("compact").GetBoolean());
            Assert.Equal(200000, json.GetProperty("output_byte_limit").GetInt32());
            var nextCommands = json.GetProperty("next_commands").EnumerateArray()
                .Select(command => command.GetString()!)
                .ToList();
            Assert.NotEmpty(nextCommands);
            Assert.All(nextCommands, command =>
            {
                Assert.Contains("--db ", command, StringComparison.Ordinal);
                Assert.Contains("--path 'src/**'", command, StringComparison.Ordinal);
                Assert.Contains("--max-json-bytes 200000", command, StringComparison.Ordinal);
            });
            Assert.True(json
                .GetProperty("truncation")
                .GetProperty("sections")
                .GetProperty("top_files")
                .GetProperty("truncated")
                .GetBoolean());

            var (jsonExitCode, jsonStdout, jsonStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--summary-only", "--path", "src/**", "--max-json-bytes", "200000"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr);
            using var jsonDocument = ParseJsonOutput(jsonStdout);
            Assert.True(jsonDocument.RootElement.GetProperty("summary_only").GetBoolean());
            Assert.Equal(200000, jsonDocument.RootElement.GetProperty("output_byte_limit").GetInt32());

            var (capExitCode, capStdout, capStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--compact", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, capExitCode);
            Assert.Equal(string.Empty, capStderr);
            using var capDocument = ParseJsonOutput(capStdout);
            var capError = capDocument.RootElement;
            Assert.Equal("E028_RESPONSE_BUDGET_TOO_SMALL", capError.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", capError.GetProperty("category").GetString());
            Assert.Equal("map", capError.GetProperty("command").GetString());
            Assert.StartsWith("cdidx map ", capError.GetProperty("usage").GetString(), StringComparison.Ordinal);
            Assert.Equal(1, capError.GetProperty("requested_bytes").GetInt64());
            Assert.Equal(1, capError.GetProperty("effective_bytes").GetInt64());
            Assert.True(capError.GetProperty("minimum_required_bytes_known").GetBoolean());
            Assert.False(capError.GetProperty("minimum_required_bytes_uncertain").GetBoolean());
            var minimumRequiredBytes = capError.GetProperty("minimum_required_bytes").GetInt64();
            Assert.True(minimumRequiredBytes > 1);
            Assert.Equal(
                minimumRequiredBytes,
                capError.GetProperty("retry").GetProperty("recommended_bytes").GetInt64());

            var (exactExitCode, exactStdout, exactStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                [
                    "--db", dbPath, "--compact", "--max-json-bytes",
                    minimumRequiredBytes.ToString(CultureInfo.InvariantCulture),
                ],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exactExitCode);
            Assert.Equal(string.Empty, exactStderr);
            Assert.Equal(minimumRequiredBytes, Encoding.UTF8.GetByteCount(exactStdout));
            using var exactDocument = ParseJsonOutput(exactStdout);
            Assert.Equal(
                minimumRequiredBytes,
                exactDocument.RootElement.GetProperty("output_byte_limit").GetInt64());

            var (zeroExitCode, zeroStdout, zeroStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--max-json-bytes", "0"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, zeroExitCode);
            Assert.Equal(string.Empty, zeroStderr);
            using var zeroDocument = ParseJsonOutput(zeroStdout);
            var zeroError = zeroDocument.RootElement;
            Assert.Equal("E028_RESPONSE_BUDGET_TOO_SMALL", zeroError.GetProperty("error_code").GetString());
            Assert.Equal(0, zeroError.GetProperty("requested_bytes").GetInt64());
            Assert.Equal(JsonValueKind.Null, zeroError.GetProperty("effective_bytes").ValueKind);
            Assert.False(zeroError.GetProperty("minimum_required_bytes_known").GetBoolean());
            Assert.Equal(
                "normal_payload_not_materialized",
                zeroError.GetProperty("minimum_required_bytes_unavailable_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_CompactJsonIncludesDecompositionPlanWhenDocumentExists_Issue4306()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_plan_4306");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.WriteTextFile(projectRoot, "docs/large-file-decomposition-plan.md", "# Large File Decomposition Plan\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/App.cs",
                "csharp",
                "namespace App; public class Program { public static void Main() { } }\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--compact", "--sections", "hotspots", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = ParseJsonOutput(stdout);
            var plan = document.RootElement.GetProperty("decomposition_plan");
            Assert.Equal("docs/large-file-decomposition-plan.md", plan.GetProperty("path").GetString());
            Assert.Contains("oversized source files", plan.GetProperty("description").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_SectionsHotspotsJson_MapsSectionToReturnedProperties_Issue3938()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_hotspots_section");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var i = 0; i < QueryCommandRunner.DefaultCompactSectionLimit + 2; i++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/module{i}/App{i}.cs",
                    "csharp",
                    $"namespace Module{i}; public class App{i} {{ public static void Main() {{ }} public void Run() {{ }} }}\n");
            }

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--sections", "hotspots", "--compact", "--limit", "2", "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;
            var mappedProperties = json
                .GetProperty("section_properties")
                .GetProperty("hotspots")
                .EnumerateArray()
                .Select(item => item.GetString())
                .ToArray();
            var truncationProperties = json
                .GetProperty("truncation")
                .GetProperty("sections")
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray();

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(["hotspots"], json.GetProperty("sections").EnumerateArray().Select(item => item.GetString()).ToArray());
            Assert.Equal(["top_files", "symbol_rich_files", "reference_rich_files", "entrypoints"], mappedProperties);
            Assert.True(json.TryGetProperty("top_files", out _));
            Assert.True(json.TryGetProperty("symbol_rich_files", out _));
            Assert.True(json.TryGetProperty("reference_rich_files", out _));
            Assert.True(json.TryGetProperty("entrypoints", out _));
            Assert.False(json.TryGetProperty("languages", out _));
            Assert.False(json.TryGetProperty("modules", out _));
            Assert.False(json.TryGetProperty("largest_files", out _));
            Assert.Equal(
                mappedProperties.OrderBy(property => property, StringComparer.Ordinal),
                truncationProperties.OrderBy(property => property, StringComparer.Ordinal));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJsonIncludesCurrentAndLegacyWorkspaceMetadataForProjectDb_Issue4573()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            var expectedBranch = TestProjectHelper.RunGit(projectRoot, "rev-parse", "--abbrev-ref", "HEAD").Trim();
            var legacyHead = new string('a', 40);
            var nextLegacyHead = new string('b', 40);
            var nextIndexedHead = new string('c', 40);
            var indexedHeadTimestamp = DateTimeOffset.Parse("2026-07-17T01:02:03Z", CultureInfo.InvariantCulture);
            var nextIndexedHeadTimestamp = indexedHeadTimestamp.AddMinutes(1);
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues(
                    (DbContext.IndexedHeadCommitMetaKey, legacyHead),
                    (DbContext.WorkspaceVerifiedHeadShaMetaKey, expectedHead),
                    (DbContext.IndexedHeadShaMetaKey, expectedHead),
                    (DbContext.IndexedHeadBranchMetaKey, expectedBranch),
                    (DbContext.IndexedHeadTimestampMetaKey, indexedHeadTimestamp.ToString("O", CultureInfo.InvariantCulture)));
            }
            RepoMapBuilder.HeadMetadataCapturedForTesting.Value = () =>
            {
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                var writer = new DbWriter(db.Connection);
                writer.SetMetaValues(
                    (DbContext.IndexedProjectRootMetaKey, Path.Combine(projectRoot, "after-map-snapshot")),
                    (DbContext.IndexedHeadCommitMetaKey, nextLegacyHead),
                    (DbContext.IndexedHeadShaMetaKey, nextIndexedHead),
                    (DbContext.IndexedHeadTimestampMetaKey, nextIndexedHeadTimestamp.ToString("O", CultureInfo.InvariantCulture)));
            };

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--summary-only"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.False(json.GetProperty("git_is_dirty").GetBoolean());
            Assert.Equal(legacyHead, json.GetProperty("indexed_head_commit").GetString());
            Assert.Equal(expectedHead, json.GetProperty("workspace_verified_head_sha").GetString());
            Assert.Equal(expectedHead, json.GetProperty("indexed_head_sha").GetString());
            Assert.Equal(expectedBranch, json.GetProperty("indexed_head_branch").GetString());
            Assert.Equal(indexedHeadTimestamp, json.GetProperty("indexed_head_timestamp").GetDateTimeOffset());
            Assert.Equal(0, json.GetProperty("commits_ahead_of_indexed_head").GetInt32());
            var headFreshness = json.GetProperty("head_freshness");
            Assert.Equal("head_current", headFreshness.GetProperty("state").GetString());
            Assert.Equal("workspace", headFreshness.GetProperty("scope").GetString());
            Assert.Equal("workspace_verified", headFreshness.GetProperty("indexed_head_source").GetString());
            Assert.Equal(expectedHead, headFreshness.GetProperty("workspace_verified_head").GetString());
            Assert.Equal(expectedHead, headFreshness.GetProperty("latest_index_head").GetString());
            Assert.Equal(legacyHead, headFreshness.GetProperty("legacy_full_scan_head").GetString());
        }
        finally
        {
            RepoMapBuilder.HeadMetadataCapturedForTesting.Value = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void HeadFreshness_DoesNotMixLatestBranchContextWithOlderWorkspaceVerification_Issue5054()
    {
        var verifiedHead = new string('a', 40);
        var latestHead = new string('b', 40);
        var latestTimestamp = DateTimeOffset.Parse("2026-08-10T01:02:03Z", CultureInfo.InvariantCulture);
        var freshness = StatusHeadFreshness.FromMap(new RepoMapResult
        {
            GitHead = verifiedHead,
            IndexedHeadCommit = new string('c', 40),
            WorkspaceVerifiedHeadSha = verifiedHead,
            IndexedHeadSha = latestHead,
            IndexedHeadBranch = "latest-write-branch",
            IndexedHeadTimestamp = latestTimestamp,
            WorktreeHeadChanged = false,
            CommitsAheadOfIndexedHead = 2,
        });

        Assert.NotNull(freshness);
        Assert.Equal(verifiedHead, freshness.IndexedHead);
        Assert.Equal("workspace_verified", freshness.IndexedHeadSource);
        Assert.Equal(latestHead, freshness.LatestIndexHead);
        Assert.Null(freshness.IndexedHeadBranch);
        Assert.Null(freshness.IndexedHeadTimestamp);
        Assert.Null(freshness.CommitsAheadOfIndexedHead);
    }

    [Fact]
    public void RunMap_WithJson_CSharpRawStringFixturesDoNotCreatePhantomEntrypoints()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_raw_string");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/fixture.cs",
                "csharp",
                """"
                public class FixtureHost
                {
                    public void UsesRawFixture()
                    {
                        const string fixture = """
                            function main()
                            end

                            public class App
                            {
                            }
                            """;
                    }
                }
                """");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var entrypoints = document.RootElement.GetProperty("entrypoints");

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Empty(entrypoints.EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_JavaModuleInfoUsesModuleDeclarationAsModuleKey()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_java_module");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "com", "example", "app"));
            File.WriteAllText(
                Path.Combine(projectRoot, "module-info.java"),
                """
                module com.example.app {
                    requires java.base;
                    exports com.example.api;
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "com", "example", "app", "App.java"),
                """
                package com.example.app;

                public class App
                {
                    public static void main(String[] args) {}
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var javaModule = Assert.Single(modules.Where(module => module.GetProperty("module").GetString() == "com.example.app"));
            Assert.Equal(2, javaModule.GetProperty("files").GetInt32());
            Assert.DoesNotContain(modules, module => module.GetProperty("module").GetString() == "com");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_JavaModuleInfoWithAllmanBraceUsesModuleDeclarationAsModuleKey()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_java_module_allman");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "com", "example", "app"));
            File.WriteAllText(
                Path.Combine(projectRoot, "module-info.java"),
                """
                module com.example.app
                {
                    requires java.base;
                    exports com.example.api;
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "com", "example", "app", "App.java"),
                """
                package com.example.app;

                public class App
                {
                    public static void main(String[] args) {}
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var javaModule = Assert.Single(modules.Where(module => module.GetProperty("module").GetString() == "com.example.app"));
            Assert.Equal(2, javaModule.GetProperty("files").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_NonJavaNamespaceDoesNotOverridePathBasedModuleKey()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_non_java_namespace");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "src", "App"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "App", "App.cs"),
                """
                namespace My.Company.App;

                public class App {}
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains(modules, module => module.GetProperty("module").GetString() == "src/App");
            Assert.DoesNotContain(modules, module => module.GetProperty("module").GetString() == "My.Company.App");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJson_PathFilteredJavaModuleFileKeepsModuleDeclarationAsModuleKey()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_java_module_filtered");
        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "com", "example", "app"));
            File.WriteAllText(
                Path.Combine(projectRoot, "module-info.java"),
                """
                module com.example.app {
                    requires java.base;
                    exports com.example.api;
                }
                """);
            File.WriteAllText(
                Path.Combine(projectRoot, "com", "example", "app", "App.java"),
                """
                package com.example.app;

                public class App
                {
                    public static void main(String[] args) {}
                }
                """);

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--json", "--quiet"],
                _jsonOptions));
            var dbPath = Path.Combine(projectRoot, ".cdidx", "codeindex.db");
            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json", "--path", "com/example/app/App.java"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var modules = document.RootElement.GetProperty("modules").EnumerateArray().ToList();
            var topFiles = document.RootElement.GetProperty("top_files").EnumerateArray().ToList();

            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var javaModule = Assert.Single(modules);
            Assert.Equal("com.example.app", javaModule.GetProperty("module").GetString());
            Assert.Equal(1, javaModule.GetProperty("files").GetInt32());
            var topFile = Assert.Single(topFiles);
            Assert.Equal("com/example/app/App.java", topFile.GetProperty("path").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_WithJsonIncludesWorkspaceMetadataForCustomDbUnderCdidx()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_custom_db");
        var dbContainerRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_custom_container");
        var dbPath = Path.Combine(dbContainerRoot, ".cdidx", "shared.db");
        try
        {
            TestProjectHelper.InitializeGitRepo(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(Path.Combine(projectRoot, "src", "app.cs"), "class App {}\n");
            TestProjectHelper.RunGit(projectRoot, "add", "src/app.cs");
            TestProjectHelper.RunGit(projectRoot, "commit", "-m", "initial");

            var expectedHead = TestProjectHelper.RunGit(projectRoot, "rev-parse", "HEAD").Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                db.InitializeSchema();
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(DbContext.IndexedProjectRootMetaKey, projectRoot);
            }
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--json"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(projectRoot, json.GetProperty("project_root").GetString());
            Assert.Equal(expectedHead, json.GetProperty("git_head").GetString());
            Assert.False(json.GetProperty("git_is_dirty").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(dbContainerRoot);
        }
    }

    [Fact]
    public void RunMap_NonexistentPathReturnsNotFound()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_notfound");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--path", "nonexistent/"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("No files found", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_NonexistentPathJsonReturnsNotFoundWithPayload()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_notfound_json");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/app.cs", "csharp", "class App {}\n");

            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--path", "nonexistent/", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            using var document = ParseJsonOutput(stdout);
            Assert.Equal(0, document.RootElement.GetProperty("file_count").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("decomposition_plan", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_EmptyDbWithoutFiltersReturnsSuccess()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_empty_ok");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            // No files inserted — empty but valid index / ファイル未挿入 — 空だが有効なインデックス

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("Files      : 0", stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunMap_HumanLargestFilesFormatsSizesAndBytesFlagKeepsRawCounts()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_query_runner_map_size_units");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/big.cs", "csharp", "class Big {}\n");
            SetIndexedFileSize(dbPath, "src/big.cs", 5L * 1024 * 1024 * 1024);

            var (formattedExit, formattedStdout, formattedStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath],
                _jsonOptions));
            var (rawExit, rawStdout, rawStderr) = CaptureConsole(() => QueryCommandRunner.RunMap(
                ["--db", dbPath, "--bytes"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, formattedExit);
            Assert.Equal(CommandExitCodes.Success, rawExit);
            Assert.Equal(string.Empty, formattedStderr);
            Assert.Equal(string.Empty, rawStderr);
            Assert.Contains("Largest files:", formattedStdout);
            Assert.Contains("src/big.cs", formattedStdout);
            Assert.Contains("5.0 GiB", formattedStdout);
            Assert.Contains("5368709120 bytes", rawStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
