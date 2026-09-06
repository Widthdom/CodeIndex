using System.Text.Json;
using CodeIndex.Cli;
using Microsoft.Data.Sqlite;
using Xunit;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerAuditContinuationIssue5278Tests
{
    private static SearchAuditRecipe Recipe(string name) => new(name, name,
        [new SearchAuditRecipeQuery("needle", "Needle5278", "needle", [], "Review", ExactSubstring: true)]);

    [Fact]
    public void AuditContinuation_SmallPagesPreserveEveryAttributedObservationAndExactBoundary()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        for (var i = 0; i < 6; i++)
            TestProjectHelper.InsertIndexedFile(db, $"src/File{i}.cs", "csharp", $"class Needle5278_{i} {{ }}\n");
        var recipes = new[] { Recipe("first"), Recipe("second") };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        var finished = false;
        for (var page = 0; page < 20; page++)
        {
            string[] args = ["--all", "--db", db, "--json", "--limit", "3", "--total-limit", "1"];
            if (token != null) args = [.. args, "--continuation", token];
            var (exit, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes));
            using var result = JsonDocument.Parse(stdout);
            var root = result.RootElement;
            foreach (var recipe in root.GetProperty("recipes").EnumerateArray())
                foreach (var query in recipe.GetProperty("queries").EnumerateArray())
                    foreach (var row in query.GetProperty("results").EnumerateArray())
                        Assert.True(seen.Add(recipe.GetProperty("name").GetString() + ":" + row.GetProperty("path").GetString()), stdout);
            token = root.GetProperty("continuation").GetProperty("next_token").GetString();
            if (token == null)
            {
                Assert.Equal(CommandExitCodes.Success, exit);
                Assert.True(root.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
                finished = true;
                break;
            }
            Assert.Equal(CommandExitCodes.PartialResult, exit);
            Assert.Contains("cdidx audit --all", root.GetProperty("continuation").GetProperty("next_command").GetString());
        }
        Assert.True(finished);
        Assert.Equal(12, seen.Count);

        var (fullExit, full, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", db, "--json", "--limit", "20", "--total-limit", "12"], JsonOptions, recipes));
        using var fullResult = JsonDocument.Parse(full);
        Assert.Equal(CommandExitCodes.Success, fullExit);
        Assert.False(fullResult.RootElement.GetProperty("summary").GetProperty("truncated").GetBoolean());
        Assert.Equal(12, fullResult.RootElement.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
    }

    [Fact]
    public void AuditContinuation_RejectsChangedScopeRecipeGenerationAndCorruptionBeforeQueries()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_validation_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/One.cs", "csharp", "class Needle5278 { }\n");
        var recipes = new[] { Recipe("first"), Recipe("second") };
        string[] args = ["--all", "--db", db, "--json", "--limit", "1", "--total-limit", "1"];
        var (_, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes));
        using var result = JsonDocument.Parse(stdout);
        var token = result.RootElement.GetProperty("continuation").GetProperty("next_token").GetString()!;
        void Reject(string[] candidate, SearchAuditRecipe[] selected)
        {
            var calls = 0;
            var (exit, error, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(candidate, JsonOptions, selected,
                beforeQueryForTesting: _ => calls++));
            Assert.Equal(CommandExitCodes.UsageError, exit);
            Assert.Equal(0, calls);
            Assert.Contains("continuation", error);
        }
        Reject([.. args, "--continuation", "audit:v1:bad"], recipes);
        var (compactExit, compactError, compactStderr) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", db, "--format", "compact", "--continuation", "bad"], JsonOptions, recipes));
        using var compactDocument = JsonDocument.Parse(compactError);
        Assert.Equal(CommandExitCodes.UsageError, compactExit);
        Assert.Empty(compactStderr);
        Assert.Equal("E010_USAGE_ERROR", compactDocument.RootElement.GetProperty("error_code").GetString());
        Reject([.. args, "--continuation", token, "--path", "other/**"], recipes);
        Reject([.. args, "--continuation", token], [Recipe("changed"), recipes[1]]);
        Reject([.. args, "--continuation", token],
            [recipes[0] with { Queries = [recipes[0].Queries[0] with { Query = "ChangedDefinition5278" }] }, recipes[1]]);
        Reject([.. args, "--continuation", token, "--limit", "2"], recipes);
        var corrupted = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(token["audit:v1:".Length..]))!;
        corrupted["offsets"]![0] = 2;
        Reject([.. args, "--continuation", "audit:v1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(corrupted.ToJsonString()))], recipes);
        Reject([.. args, "--continuation", token, "--continuation", token], recipes);
        Reject([.. args, "--continuation", "audit:v1:" + new string('x', 16384)], recipes);
        TestProjectHelper.InsertIndexedFile(db, "src/Two.cs", "csharp", "class Needle5278Two { }\n");
        Reject([.. args, "--continuation", token], recipes);
    }

    [Fact]
    public void AuditContinuation_SeparatesExecutionEmissionAndIntentionalSelection()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_selection_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        for (var i = 0; i < 4; i++)
            TestProjectHelper.InsertIndexedFile(db, $"src/File{i}.cs", "csharp", $"class Needle5278_{i} {{ }}\n");
        var recipes = new[] { Recipe("first") };
        foreach (var optIn in new[] { false, true })
        {
            string[] args = ["--all", "--db", db, "--json", "--limit", "1", "--total-limit", "100"];
            if (optIn) args = [.. args, "--allow-partial"];
            var (exit, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes));
            using var result = JsonDocument.Parse(stdout);
            var summary = result.RootElement.GetProperty("summary");
            Assert.Equal(optIn ? CommandExitCodes.Success : CommandExitCodes.PartialResult, exit);
            Assert.True(summary.GetProperty("execution_complete").GetBoolean());
            Assert.False(summary.GetProperty("observation_emission_complete").GetBoolean());
            Assert.True(summary.GetProperty("truncated").GetBoolean());
        }
        var (sampleExit, sample, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", db, "--json", "--limit", "10", "--sample", "1"], JsonOptions, recipes));
        using var sampled = JsonDocument.Parse(sample);
        Assert.Equal(CommandExitCodes.Success, sampleExit);
        Assert.Equal(3, sampled.RootElement.GetProperty("summary").GetProperty("intentional_selection_omitted_count").GetInt32());
        Assert.True(sampled.RootElement.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
    }

    [Fact]
    public void AuditContinuation_ByteAdmissionResumesOnlyEmittedRowsInJsonAndNdjson()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_bytes_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        for (var i = 0; i < 12; i++)
            TestProjectHelper.InsertIndexedFile(db, $"src/File{i}.cs", "csharp",
                $"class Needle5278_{i} {{ string Text = \"{new string('x', 500)}\"; }}\n");
        var recipes = new[] { Recipe("first"), Recipe("second") };
        foreach (var format in new[] { "--json", "--json=ndjson" })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? token = null;
            var finished = false;
            for (var page = 0; page < 30; page++)
            {
                string[] args = ["--all", "--db", db, format, "--limit", "20", "--total-limit", "20", "--max-line-width", "0", "--max-json-bytes", "16384"];
                if (token != null) args = [.. args, "--continuation", token];
                var (exit, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes));
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(stdout) <= 16384, stdout);
                var lines = format == "--json" ? [stdout] : stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                using var terminal = JsonDocument.Parse(lines[^1]);
                if (format == "--json")
                {
                    foreach (var recipe in terminal.RootElement.GetProperty("recipes").EnumerateArray())
                        foreach (var query in recipe.GetProperty("queries").EnumerateArray())
                            foreach (var row in query.GetProperty("results").EnumerateArray())
                                Assert.True(seen.Add(row.GetProperty("recipe").GetString() + ":" + row.GetProperty("path").GetString()), stdout);
                }
                else
                {
                    foreach (var line in lines[..^1])
                    {
                        using var row = JsonDocument.Parse(line);
                        Assert.True(seen.Add(row.RootElement.GetProperty("recipe").GetString() + ":" + row.RootElement.GetProperty("path").GetString()), stdout);
                    }
                }
                token = terminal.RootElement.GetProperty("continuation").GetProperty("next_token").GetString();
                if (token == null) { Assert.Equal(CommandExitCodes.Success, exit); finished = true; break; }
                Assert.Equal(CommandExitCodes.PartialResult, exit);
            }
            Assert.True(finished);
            Assert.Equal(24, seen.Count);
        }
    }

    [Fact]
    public void AuditContinuation_FailureAndCancellationKeepUnaccountedChildrenPending()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_failure_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/One.cs", "csharp", "class Needle5278 { }\n");
        var recipes = new[] { Recipe("first"), Recipe("second") };
        string[] args = ["--all", "--db", db, "--json", "--limit", "10"];
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var calls = 0;
            var (exit, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes,
                cancellation.Token,
                afterQueryForTesting: () => { if (cancel) cancellation.Cancel(); },
                beforeQueryForTesting: _ => { if (!cancel && calls++ == 0) throw new InvalidOperationException("injected"); }));
            Assert.Equal(cancel ? CommandExitCodes.CancelledBySignal : CommandExitCodes.PartialResult, exit);
            using var first = JsonDocument.Parse(stdout);
            var token = first.RootElement.GetProperty("continuation").GetProperty("next_token").GetString()!;
            var (resumedExit, resumed, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
                [.. args, "--continuation", token], JsonOptions, recipes));
            using var result = JsonDocument.Parse(resumed);
            Assert.Equal(CommandExitCodes.Success, resumedExit);
            Assert.Equal(1, result.RootElement.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
            Assert.True(result.RootElement.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
        }
    }

    [Fact]
    public void AuditContinuation_ExhaustedCountWindowHasBoundedFallbackAndEmptyWorkCompletes()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_window_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        var recipes = new[] { Recipe("first") };
        string[] args = ["--all", "--db", db, "--json", "--count", "--limit", "1"];
        var (emptyExit, empty, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes));
        using var emptyResult = JsonDocument.Parse(empty);
        Assert.Equal(CommandExitCodes.Success, emptyExit);
        Assert.True(emptyResult.RootElement.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
        for (var i = 0; i <= QueryCommandRunner.FetchLimitForSearchEnvelopeForTests(1); i++)
            TestProjectHelper.InsertIndexedFile(db, $"src/File{i}.cs", "csharp", $"class Needle5278_{i} {{ }}\n");
        var (exit, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes));
        using var result = JsonDocument.Parse(stdout);
        Assert.Equal(CommandExitCodes.PartialResult, exit);
        var continuation = result.RootElement.GetProperty("continuation");
        Assert.Equal(JsonValueKind.Null, continuation.GetProperty("next_token").ValueKind);
        Assert.Equal(1, continuation.GetProperty("fallback_count").GetInt32());
        Assert.Equal("child_coverage_not_authoritative", continuation.GetProperty("fallbacks")[0].GetProperty("reason").GetString());
        Assert.Contains("--include-query needle", continuation.GetProperty("fallbacks")[0].GetProperty("command").GetString());
        Assert.True(result.RootElement.GetProperty("summary").GetProperty("execution_complete").GetBoolean());
        Assert.False(result.RootElement.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
    }

    [Fact]
    public void AuditContinuation_DeduplicatedRawCapCannotClaimCompleteCoverage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_dedup_cap_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/A.cs", "csharp", "class Needle5278 { }\n");
        TestProjectHelper.InsertIndexedFile(db, "src/Z.cs", "csharp", "class Needle5278Beyond { }\n");
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE positions(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM positions WHERE n < 9999)
                INSERT INTO chunks(file_id, chunk_index, start_line, end_line, content)
                SELECT f.id, positions.n, 1, 1, 'class Needle5278 { }' FROM positions, files f WHERE f.path = 'src/A.cs';
                """;
            command.ExecuteNonQuery();
        }
        var (exit, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", db, "--json", "--limit", "10"], JsonOptions, [Recipe("first")]));
        using var result = JsonDocument.Parse(stdout);
        Assert.Equal(CommandExitCodes.PartialResult, exit);
        Assert.Equal(1, result.RootElement.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
        var recipe = result.RootElement.GetProperty("recipes")[0];
        var query = recipe.GetProperty("queries")[0];
        Assert.False(query.GetProperty("source_total_authoritative").GetBoolean());
        Assert.True(query.GetProperty("count_approximate").GetBoolean());
        Assert.Equal(1, query.GetProperty("source_total_lower_bound").GetInt32());
        Assert.False(recipe.GetProperty("count_authoritative").GetBoolean());
        Assert.False(result.RootElement.GetProperty("summary").GetProperty("count_authoritative").GetBoolean());
        Assert.True(result.RootElement.GetProperty("recipes")[0].GetProperty("queries")[0].GetProperty("candidate_window_exhausted").GetBoolean());
        Assert.False(result.RootElement.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
        Assert.Equal(1, result.RootElement.GetProperty("continuation").GetProperty("fallback_count").GetInt32());
    }

    [Fact]
    public void AuditContinuation_QueryDetailOmissionsStayPendingWithTargetedFallback()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_details_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/One.cs", "csharp", "class Needle5278 { }\n");
        var recipes = Enumerable.Range(0, 9).Select(r => new SearchAuditRecipe($"recipe{r}", "details",
            Enumerable.Range(0, 64).Select(q => new SearchAuditRecipeQuery($"query{q}",
                r == 8 && q == 63 ? "Needle5278" : "Missing5278", "query", [], "Review", ExactSubstring: true)).ToList())).ToArray();
        var (exit, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", db, "--json", "--limit", "10"], JsonOptions, recipes));
        using var result = JsonDocument.Parse(stdout);
        var root = result.RootElement;
        Assert.Equal(CommandExitCodes.PartialResult, exit);
        Assert.Equal(0, root.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
        Assert.Equal(1, root.GetProperty("query_details").GetProperty("omitted_result_count").GetInt32());
        Assert.True(root.GetProperty("summary").GetProperty("execution_complete").GetBoolean());
        Assert.False(root.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
        var continuation = root.GetProperty("continuation");
        Assert.Equal("continuation_query_limit", continuation.GetProperty("unavailable_reason").GetString());
        Assert.Equal(1, continuation.GetProperty("fallback_count").GetInt32());
        Assert.Equal("recipe8", continuation.GetProperty("fallbacks")[0].GetProperty("recipe").GetString());
        Assert.Equal("query63", continuation.GetProperty("fallbacks")[0].GetProperty("query_name").GetString());

        var (ndjsonExit, ndjson, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", db, "--json=ndjson", "--limit", "10"], JsonOptions, recipes));
        using var terminal = JsonDocument.Parse(ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.Equal(CommandExitCodes.Success, ndjsonExit);
        Assert.Equal(1, terminal.RootElement.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
        Assert.True(terminal.RootElement.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
    }

    [Fact]
    public void AuditContinuation_NdjsonAcceptsCompleteResponseAcrossMetadataSizeTransitions()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_resume_ndjson_boundary_5278");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        for (var i = 0; i < 4; i++)
            TestProjectHelper.InsertIndexedFile(db, $"src/File{i}.cs", "csharp", $"class Needle5278_{i} {{ }}\n");
        var recipes = new[] { Recipe("first"), Recipe("second") };
        string[] args = ["--all", "--db", db, "--json=ndjson", "--search-fields", "path", "--limit", "10"];
        var (fullExit, full, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes));
        Assert.Equal(CommandExitCodes.Success, fullExit);
        var budget = System.Text.Encoding.UTF8.GetByteCount(full) + 128; // Timing and explicit-budget fields can vary.
        var (exit, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            [.. args, "--max-json-bytes", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)], JsonOptions, recipes));
        Assert.Equal(CommandExitCodes.Success, exit);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(stdout) <= budget);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(9, lines.Length);
        using var terminal = JsonDocument.Parse(lines[^1]);
        Assert.True(terminal.RootElement.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
    }
}
