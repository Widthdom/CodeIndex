using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public class QueryCommandRunnerAuditSarifIssue4903Tests
{
    [Fact]
    public void RunSearch_RecipeSarifMaxJsonBytesUsesExactUtf8BudgetAndWholeResults_Issue4903()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_audit_sarif_bytes_4903");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 0; index < 8; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/日本語/診断猫{index:D2}.cs",
                    "csharp",
                    $$"""
                    public sealed class 診断猫{{index:D2}}
                    {
                        public void Run(Exception ex)
                        {
                            JsonDocument.Parse("{}");
                            Console.WriteLine(ex.Message);
                        }
                    }
                    """);
            }

            string[] args =
            [
                "--recipe", "risky-code",
                "--include-query", "unbounded-json-parse",
                "--include-query", "raw-diagnostic-echo",
                "--db", dbPath,
                "--format", "sarif",
                "--origin", "code",
                "--limit", "20",
            ];
            var (unboundedExitCode, unboundedStdout, unboundedStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(args, JsonOptions));
            var exactBudget = Encoding.UTF8.GetByteCount(unboundedStdout);

            Assert.Equal(CommandExitCodes.Success, unboundedExitCode);
            Assert.Equal(string.Empty, unboundedStderr);
            Assert.Equal(16, ReadSarifResults(unboundedStdout).Length);

            var exactArgs = args.Concat(["--max-json-bytes", exactBudget.ToString()]).ToArray();
            var (exactExitCode, exactStdout, exactStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(exactArgs, JsonOptions));

            Assert.Equal(CommandExitCodes.Success, exactExitCode);
            Assert.Equal(string.Empty, exactStderr);
            Assert.Equal(unboundedStdout, exactStdout);
            Assert.Equal(exactBudget, Encoding.UTF8.GetByteCount(exactStdout));

            var boundedBudget = exactBudget - 1;
            var boundedArgs = args.Concat(["--max-json-bytes", boundedBudget.ToString()]).ToArray();
            var (boundedExitCode, boundedStdout, boundedStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(boundedArgs, JsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, boundedExitCode);
            Assert.Equal(string.Empty, boundedStderr);
            Assert.InRange(Encoding.UTF8.GetByteCount(boundedStdout), 1, boundedBudget);
            using var boundedDocument = JsonDocument.Parse(boundedStdout);
            var run = boundedDocument.RootElement.GetProperty("runs")[0];
            var results = run.GetProperty("results").EnumerateArray().ToArray();
            var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray().ToArray();
            var properties = run.GetProperty("properties");
            var byteBudget = properties.GetProperty("byte_budget");
            var querySummaries = properties.GetProperty("queries").EnumerateArray().ToArray();

            Assert.InRange(results.Length, 1, 15);
            Assert.Equal(2, rules.Length);
            Assert.All(results, result =>
            {
                Assert.StartsWith(
                    "src/日本語/",
                    result.GetProperty("locations")[0]
                        .GetProperty("physicalLocation")
                        .GetProperty("artifactLocation")
                        .GetProperty("uri")
                        .GetString(),
                    StringComparison.Ordinal);
                Assert.Contains(
                    rules,
                    rule => rule.GetProperty("id").GetString() == result.GetProperty("ruleId").GetString());
            });
            Assert.Equal(2, properties.GetProperty("query_count").GetInt32());
            Assert.Equal(2, querySummaries.Length);
            Assert.Equal(results.Length, properties.GetProperty("result_count").GetInt32());
            Assert.Equal(16, properties.GetProperty("source_result_count").GetInt32());
            Assert.False(properties.GetProperty("source_result_count_authoritative").GetBoolean());
            Assert.Equal(boundedBudget, byteBudget.GetProperty("max_json_bytes").GetInt32());
            Assert.Equal("utf8_bytes_including_final_newline", byteBudget.GetProperty("measurement").GetString());
            Assert.Equal("omit_whole_results", byteBudget.GetProperty("strategy").GetString());
            Assert.Equal(exactBudget, byteBudget.GetProperty("minimum_complete_bytes").GetInt32());
            Assert.Equal(16 - results.Length, byteBudget.GetProperty("omitted_result_count").GetInt32());
            Assert.True(byteBudget.GetProperty("truncated").GetBoolean());
            Assert.Contains(
                $"--max-json-bytes {exactBudget}",
                properties.GetProperty("replay_command").GetString(),
                StringComparison.Ordinal);
            Assert.All(querySummaries, query =>
            {
                Assert.False(query.GetProperty("source_result_count_authoritative").GetBoolean());
                Assert.True(query.GetProperty("omitted_by_byte_budget").GetInt32() >= 0);
                Assert.False(string.IsNullOrWhiteSpace(query.GetProperty("replay_command").GetString()));
            });

            var allowPartialArgs = boundedArgs.Concat(["--allow-partial"]).ToArray();
            var (allowedExitCode, allowedStdout, allowedStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(allowPartialArgs, JsonOptions));

            Assert.Equal(CommandExitCodes.Success, allowedExitCode);
            Assert.Equal(string.Empty, allowedStderr);
            Assert.InRange(Encoding.UTF8.GetByteCount(allowedStdout), 1, boundedBudget);
            using var allowedDocument = JsonDocument.Parse(allowedStdout);
            Assert.True(
                allowedDocument.RootElement.GetProperty("runs")[0]
                    .GetProperty("properties")
                    .GetProperty("byte_budget")
                    .GetProperty("truncated")
                    .GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeSarifMaxJsonBytesOmitsOversizedResultAndPreflightsMinimum_Issue4903()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_audit_sarif_oversized_4903");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var oversizedPath = $"src/{new string('猫', 1_500)}.cs";
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                oversizedPath,
                "csharp",
                "public sealed class Sample { void Run(Exception ex) { Console.WriteLine(ex.Message); } }");
            string[] args =
            [
                "--recipe", "risky-code/raw-diagnostic-echo",
                "--db", dbPath,
                "--format", "sarif",
                "--origin", "code",
                "--limit", "10",
                "--max-json-bytes", "4000",
            ];

            var (exitCode, stdout, stderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(args, JsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.InRange(Encoding.UTF8.GetByteCount(stdout), 1, 4_000);
            using var document = JsonDocument.Parse(stdout);
            var run = document.RootElement.GetProperty("runs")[0];
            Assert.Empty(run.GetProperty("results").EnumerateArray());
            Assert.Empty(run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray());
            var properties = run.GetProperty("properties");
            var byteBudget = properties.GetProperty("byte_budget");
            Assert.Equal(0, byteBudget.GetProperty("emitted_result_count").GetInt32());
            Assert.Equal(1, byteBudget.GetProperty("omitted_result_count").GetInt32());
            Assert.True(byteBudget.GetProperty("first_omitted_result_bytes").GetInt32() > 4_000);
            Assert.Equal(
                "response_byte_budget",
                properties.GetProperty("scope").GetProperty("coverage_omitted_reason").GetString());
            Assert.Contains(
                "--recipe risky-code/raw-diagnostic-echo",
                properties.GetProperty("replay_command").GetString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "--recipe risky-code/raw-diagnostic-echo",
                properties.GetProperty("next_commands")[0].GetString(),
                StringComparison.Ordinal);
            var replayOptions = QueryCommandRunner.ParseArgs(args, jsonDefault: false);
            var maximumSupportedReplay = QueryCommandRunner.BuildSearchRecipeSarifReplayCommandForTests(
                "risky-code/raw-diagnostic-echo",
                replayOptions,
                16 * 1024 * 1024);
            var aboveMaximumReplay = QueryCommandRunner.BuildSearchRecipeSarifReplayCommandForTests(
                "risky-code/raw-diagnostic-echo",
                replayOptions,
                (16 * 1024 * 1024) + 1);
            Assert.Contains("--max-json-bytes 16777216", maximumSupportedReplay, StringComparison.Ordinal);
            Assert.DoesNotContain("--max-json-bytes", aboveMaximumReplay, StringComparison.Ordinal);

            var tooSmallArgs = args
                .Take(args.Length - 1)
                .Append("1")
                .ToArray();
            var (tooSmallExitCode, tooSmallStdout, tooSmallStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(tooSmallArgs, JsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, tooSmallExitCode);
            Assert.Equal(string.Empty, tooSmallStdout);
            Assert.Contains("minimum schema-valid bounded audit SARIF output requires", tooSmallStderr, StringComparison.Ordinal);
            Assert.Contains("no partial SARIF was written", tooSmallStderr, StringComparison.Ordinal);

            var (explicitJsonExitCode, explicitJsonStdout, explicitJsonStderr) = CaptureConsole(
                () => ProgramRunner.Run(
                    [
                        "audit", "risky-code/raw-diagnostic-echo",
                        "--db", dbPath,
                        "--format", "sarif",
                        "--json",
                        "--origin", "code",
                        "--limit", "10",
                        "--max-json-bytes", "1000",
                    ],
                    appVersion: "1.10.0"));

            Assert.Equal(CommandExitCodes.UsageError, explicitJsonExitCode);
            Assert.Equal(string.Empty, explicitJsonStderr);
            Assert.InRange(Encoding.UTF8.GetByteCount(explicitJsonStdout), 1, 1_000);
            using var explicitJsonDocument = JsonDocument.Parse(explicitJsonStdout);
            Assert.Equal("E010_USAGE_ERROR", explicitJsonDocument.RootElement.GetProperty("error_code").GetString());
            Assert.Equal("audit", explicitJsonDocument.RootElement.GetProperty("command").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeSarifByteBudgetReplayPreservesSelectorAndActiveCursor_Issue4903()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_audit_sarif_cursor_4903");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 0; index < 6; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/Diagnostic{index:D2}.cs",
                    "csharp",
                    $$"""
                    public sealed class Diagnostic{{index:D2}}
                    {
                        public void Run(Exception ex) => Console.WriteLine(ex.Message);
                    }
                    """);
            }

            string[] firstPageArgs =
            [
                "--recipe", "risky-code/raw-diagnostic-echo",
                "--db", dbPath,
                "--format", "sarif",
                "--origin", "code",
                "--limit", "2",
            ];
            var (firstPageExitCode, firstPageStdout, firstPageStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(firstPageArgs, JsonOptions));

            Assert.Equal(CommandExitCodes.Success, firstPageExitCode);
            Assert.Equal(string.Empty, firstPageStderr);
            using var firstPageDocument = JsonDocument.Parse(firstPageStdout);
            var activeCursor = firstPageDocument.RootElement.GetProperty("runs")[0]
                .GetProperty("properties")
                .GetProperty("queries")[0]
                .GetProperty("next_cursor")
                .GetString();
            Assert.False(string.IsNullOrWhiteSpace(activeCursor));

            var secondPageArgs = firstPageArgs.Concat(["--cursor", activeCursor!]).ToArray();
            var (secondPageExitCode, secondPageStdout, secondPageStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(secondPageArgs, JsonOptions));
            Assert.Equal(CommandExitCodes.Success, secondPageExitCode);
            Assert.Equal(string.Empty, secondPageStderr);
            var boundedBudget = Encoding.UTF8.GetByteCount(secondPageStdout) - 1;

            var boundedArgs = secondPageArgs
                .Concat(["--max-json-bytes", boundedBudget.ToString()])
                .ToArray();
            var (boundedExitCode, boundedStdout, boundedStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(boundedArgs, JsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, boundedExitCode);
            Assert.Equal(string.Empty, boundedStderr);
            Assert.InRange(Encoding.UTF8.GetByteCount(boundedStdout), 1, boundedBudget);
            using var boundedDocument = JsonDocument.Parse(boundedStdout);
            var properties = boundedDocument.RootElement.GetProperty("runs")[0].GetProperty("properties");
            var query = properties.GetProperty("queries")[0];
            var replayCommand = properties.GetProperty("replay_command").GetString();
            var nextCommands = properties.GetProperty("next_commands").EnumerateArray().ToArray();

            Assert.False(properties.GetProperty("cursoring_available").GetBoolean());
            Assert.Equal(JsonValueKind.Null, query.GetProperty("next_cursor").ValueKind);
            Assert.True(query.GetProperty("omitted_by_byte_budget").GetInt32() > 0);
            Assert.Single(nextCommands);
            Assert.Contains("--recipe risky-code/raw-diagnostic-echo", replayCommand, StringComparison.Ordinal);
            Assert.Contains("--cursor", replayCommand, StringComparison.Ordinal);
            Assert.Contains(activeCursor!, replayCommand, StringComparison.Ordinal);
            Assert.Equal(replayCommand, nextCommands[0].GetString());
            Assert.Contains(activeCursor!, query.GetProperty("replay_command").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_EmptyRecipeSarifRequiresExactMinimumWithoutPartialJson_Issue4903()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_audit_sarif_empty_4903");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            string[] args =
            [
                "--recipe", "risky-code/raw-diagnostic-echo",
                "--db", dbPath,
                "--format", "sarif",
                "--origin", "code",
                "--limit", "10",
            ];
            var (unboundedExitCode, unboundedStdout, unboundedStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(args, JsonOptions));
            var exactBudget = Encoding.UTF8.GetByteCount(unboundedStdout);

            Assert.Equal(CommandExitCodes.Success, unboundedExitCode);
            Assert.Equal(string.Empty, unboundedStderr);
            Assert.Empty(ReadSarifResults(unboundedStdout));

            var (exactExitCode, exactStdout, exactStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(
                    args.Concat(["--max-json-bytes", exactBudget.ToString()]).ToArray(),
                    JsonOptions));
            Assert.Equal(CommandExitCodes.Success, exactExitCode);
            Assert.Equal(string.Empty, exactStderr);
            Assert.Equal(unboundedStdout, exactStdout);

            var (underExitCode, underStdout, underStderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(
                    args.Concat(["--max-json-bytes", (exactBudget - 1).ToString()]).ToArray(),
                    JsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, underExitCode);
            Assert.Equal(string.Empty, underStdout);
            Assert.Contains(
                $"requires {exactBudget} UTF-8 bytes including the final newline",
                underStderr,
                StringComparison.Ordinal);
            Assert.Contains("no partial SARIF was written", underStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void AuditSarifMaxJsonBytesIsExposedByHelpAndCompletionSchema_Issue4903()
    {
        var flag = Assert.Single(
            CliFlagSchema.GetCompletionFlagsForCommand("audit"),
            candidate => candidate.Name == "--max-json-bytes");
        Assert.Contains("schema-valid audit SARIF", flag.Description, StringComparison.Ordinal);

        var (printed, stdout, stderr) = CaptureConsole(
            () => ConsoleUi.PrintCommandUsage("audit") ? 1 : 0);
        Assert.Equal(1, printed);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("--max-json-bytes <n>", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void RunSearch_AdHocSarifMaxJsonBytesRemainsRejected_Issue4903()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_adhoc_sarif_bytes_4903");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var (exitCode, stdout, stderr) = CaptureConsole(
                () => QueryCommandRunner.RunSearch(
                    ["Needle", "--db", dbPath, "--format", "sarif", "--max-json-bytes", "4000"],
                    JsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, exitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("--max-json-bytes is only supported with JSON search output", stderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static JsonElement[] ReadSarifResults(string stdout)
    {
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToArray();
    }
}
