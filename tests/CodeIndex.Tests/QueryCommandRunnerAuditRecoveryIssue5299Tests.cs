using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using Microsoft.Data.Sqlite;
using Xunit;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerAuditRecoveryIssue5299Tests
{
    private static SearchAuditRecipe Recipe(string name = "first") => new(name, "Recovery",
        [new SearchAuditRecipeQuery("needle", "Needle5299", "Needle", [], "Review", ExactSubstring: true)]);

    private static (int Exit, JsonDocument Document, string Output) Run(string[] args, params SearchAuditRecipe[] recipes)
    {
        var (exit, output, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, recipes));
        return (exit, JsonDocument.Parse(output), output);
    }

    private static string[] Replay(JsonElement node)
        => node.GetProperty("argv").EnumerateArray().Select(value => value.GetString()!).Skip(2).ToArray();

    [Fact]
    public void TopSummary_BoundsSerializedBytesAndKeepsCompletionAndCountSemantics()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_top_5299");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/One.cs", "csharp", "class Needle5299 { }\n");
        var recipes = Enumerable.Range(0, 20).Select(i => Recipe($"recipe{i:D2}") with
        { Description = new string('x', 2000) }).ToArray();
        foreach (var format in new[] { "--json", "--json=ndjson" })
        {
            var (exit, document, output) = Run(["--all", "--db", db, format, "--summary-level", "top"], recipes);
            using (document)
            {
                Assert.Equal(CommandExitCodes.Success, exit);
                Assert.True(Encoding.UTF8.GetByteCount(output) < 16384, output);
                var root = document.RootElement;
                Assert.False(root.TryGetProperty("recipes", out _));
                Assert.Equal("top", root.GetProperty("summary_level").GetString());
                Assert.Equal(20, root.GetProperty("summary").GetProperty("completed_query_count").GetInt32());
                Assert.True(root.GetProperty("summary").GetProperty("execution_complete").GetBoolean());
                Assert.Equal("sum_of_recipe_query_observations_not_unique_matches", root.GetProperty("summary").GetProperty("count_semantics").GetString());
                Assert.Equal(20, root.GetProperty("summary").GetProperty("minimum_matched_result_count").GetInt32());
                Assert.Contains("--db", Replay(root.GetProperty("recovery").GetProperty("partition_plan")));
            }
        }
    }

    [Fact]
    public void Plan_PaginatesDeterministicallyAndExecutesExactDisjointPathsWithScopePreserved()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_plan_5299");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        var expected = Enumerable.Range(0, 12).Select(i => $"src/File{i:D2}.cs")
            .Append("src/quote' [*]? file.cs").ToHashSet(StringComparer.Ordinal);
        foreach (var path in expected.Append("other/Outside.cs").Append("src/Excluded.cs"))
            TestProjectHelper.InsertIndexedFile(db, path, "csharp", "class Needle5299 { }\n");
        var recipes = new[] { Recipe(), Recipe("second") };
        string[] args = ["--all", "--db", db, "--audit-scope", "all", "--path", "src/**", "--exclude-path", "**/Excluded.cs", "--partition-plan"];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[]? next = args;
        string? firstUnit = null;
        while (next != null)
        {
            var (exit, document, output) = Run(next, recipes);
            using (document)
            {
                Assert.Equal(CommandExitCodes.Success, exit);
                Assert.True(Encoding.UTF8.GetByteCount(output) <= QueryCommandRunner.AuditRecoveryByteLimit);
                var root = document.RootElement;
                Assert.Equal(expected.Count, root.GetProperty("eligible_path_count").GetInt32());
                Assert.True(root.GetProperty("inventory_complete").GetBoolean());
                Assert.False(root.GetProperty("coverage_authoritative").GetBoolean());
                Assert.Equal(0, root.GetProperty("covered_partition_count").GetInt32());
                foreach (var unit in root.GetProperty("units").EnumerateArray())
                {
                    var path = unit.GetProperty("path").GetString()!;
                    Assert.True(seen.Add(path));
                    firstUnit ??= unit.GetRawText();
                    var (unitExit, result, _) = Run(Replay(unit), recipes);
                    using (result)
                    {
                        Assert.Equal(CommandExitCodes.Success, unitExit);
                        var rows = result.RootElement.GetProperty("recipes").EnumerateArray()
                            .SelectMany(recipe => recipe.GetProperty("queries").EnumerateArray())
                            .SelectMany(query => query.GetProperty("results").EnumerateArray()).ToArray();
                        Assert.Equal(2, rows.Length);
                        Assert.All(rows, row => Assert.Equal(path, row.GetProperty("path").GetString()));
                        Assert.Equal(unit.GetProperty("id").GetString(), result.RootElement.GetProperty("partition").GetProperty("id").GetString());
                    }
                }
                next = root.GetProperty("next").ValueKind == JsonValueKind.Null ? null : Replay(root.GetProperty("next"));
            }
        }
        Assert.True(expected.SetEquals(seen));
        var (_, repeated, _) = Run(args, recipes);
        using (repeated)
        {
            var unit = repeated.RootElement.GetProperty("units")[0];
            Assert.Equal(firstUnit, unit.GetRawText());
            var (partialExit, firstPage, _) = Run([.. Replay(unit), "--total-limit", "1"], recipes);
            using (firstPage)
            {
                Assert.Equal(CommandExitCodes.PartialResult, partialExit);
                var token = firstPage.RootElement.GetProperty("continuation").GetProperty("next_token").GetString()!;
                Assert.Equal("pending", firstPage.RootElement.GetProperty("partition").GetProperty("state").GetString());
                var (lastExit, lastPage, _) = Run([.. Replay(unit), "--total-limit", "1", "--continuation", token], recipes);
                using (lastPage)
                {
                    Assert.Equal(CommandExitCodes.Success, lastExit);
                    Assert.True(lastPage.RootElement.GetProperty("summary").GetProperty("observation_emission_complete").GetBoolean());
                }
            }
        }
    }

    [Fact]
    public void Plan_RejectsStaleChangedScopeRecipeAndCorruptTokensBeforeQueries()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_plan_validation_5299");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/A.cs", "csharp", "class Needle5299 { }\n");
        var recipes = new[] { Recipe() };
        var (_, plan, _) = Run(["--all", "--db", db, "--partition-plan"], recipes);
        using (plan)
        {
            var args = Replay(plan.RootElement.GetProperty("units")[0]);
            void Reject(string[] candidate, SearchAuditRecipe[] selected)
            {
                var calls = 0;
                var (exit, _, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(candidate, JsonOptions, selected,
                    beforeQueryForTesting: _ => calls++));
                Assert.Equal(CommandExitCodes.UsageError, exit);
                Assert.Equal(0, calls);
            }
            Reject([.. args, "--path", "other/**"], recipes);
            Reject(args, [recipes[0] with { Queries = [recipes[0].Queries[0] with { Query = "Changed5299" }] }]);
            Reject([.. args, "--partition", "audit-plan:v1:bad"], recipes);
            Reject(["--all", "--db", db, "--partition", "audit-plan:v1:bad"], recipes);
            TestProjectHelper.InsertIndexedFile(db, "src/B.cs", "csharp", "class Needle5299 { }\n");
            Reject(args, recipes);
        }
    }

    [Fact]
    public void Plan_ChildIntersectionsGeneratedPolicyCancellationAndByteAdmission()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_plan_filters_5299");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        foreach (var path in new[] { "src/A.cs", "src/Generated.cs", "src/Other.cs", "outside/A.cs" })
            TestProjectHelper.InsertIndexedFile(db, path, "csharp", "class Needle5299 { }\n");
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE files SET generated = 1 WHERE path = 'src/Generated.cs'";
            command.ExecuteNonQuery();
        }
        var recipe = Recipe();
        recipe = recipe with { Queries = [recipe.Queries[0] with { PathPatterns = ["**/A.cs", "**/Generated.cs"] }] };
        string[] args = ["--all", "--db", db, "--audit-scope", "all", "--path", "src/**", "--partition-plan"];
        foreach (var includeGenerated in new[] { false, true })
        {
            var (exit, plan, _) = Run(includeGenerated ? [.. args, "--include-generated"] : args, recipe);
            using (plan)
            {
                Assert.Equal(CommandExitCodes.Success, exit);
                Assert.Equal(includeGenerated ? 2 : 1, plan.RootElement.GetProperty("eligible_path_count").GetInt32());
                foreach (var unit in plan.RootElement.GetProperty("units").EnumerateArray())
                {
                    var (unitExit, result, _) = Run(Replay(unit), recipe);
                    using (result)
                    {
                        Assert.Equal(CommandExitCodes.Success, unitExit);
                        Assert.Equal(1, result.RootElement.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
                    }
                }
            }
        }
        var (budgetExit, budget, _) = Run([.. args, "--max-json-bytes", "1024"], recipe);
        using (budget)
        {
            Assert.NotEqual(CommandExitCodes.Success, budgetExit);
            Assert.Equal("E028_RESPONSE_BUDGET_TOO_SMALL", budget.RootElement.GetProperty("error_code").GetString());
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var (cancelledExit, _, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(args, JsonOptions, [recipe], cancellation.Token));
        Assert.Equal(CommandExitCodes.CancelledBySignal, cancelledExit);
        var (routedExit, routed, _) = CaptureConsole(() => QueryCommandRunner.RunAudit(args, JsonOptions));
        using var routedPlan = JsonDocument.Parse(routed);
        Assert.Equal(CommandExitCodes.Success, routedExit);
        Assert.True(routedPlan.RootElement.GetProperty("available").GetBoolean());
    }

    [Fact]
    public void Plan_HugeSingleFileStaysPendingAndOverflowNeverPublishesPartialInventory()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("audit_plan_window_5299");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/A.cs", "csharp", "class Needle5299 { }\n");
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE positions(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM positions WHERE n < 9999)
                INSERT INTO chunks(file_id, chunk_index, start_line, end_line, content)
                SELECT f.id, positions.n, 1, 1, 'class Needle5299 { }' FROM positions, files f WHERE f.path = 'src/A.cs';
                """;
            command.ExecuteNonQuery();
        }
        var recipes = new[] { Recipe() };
        var (_, plan, _) = Run(["--all", "--db", db, "--partition-plan"], recipes);
        using (plan)
        {
            foreach (var optIn in new[] { false, true })
            {
                string[] args = [.. Replay(plan.RootElement.GetProperty("units")[0]), "--summary-level", "top"];
                if (optIn) args = [.. args, "--allow-partial"];
                var (exit, result, _) = Run(args, recipes);
                using (result)
                {
                    Assert.True(exit == (optIn ? CommandExitCodes.Success : CommandExitCodes.PartialResult), result.RootElement.GetRawText());
                    var receipt = result.RootElement.GetProperty("partition");
                    Assert.Equal("pending", receipt.GetProperty("state").GetString());
                    Assert.Equal("single_file_candidate_window_exhausted", receipt.GetProperty("reason").GetString());
                    Assert.False(receipt.GetProperty("coverage_authoritative").GetBoolean());
                }
            }
        }
        var tooMany = Enumerable.Range(0, 513).Select(i => Recipe($"recipe{i}")).ToArray();
        var (overflowExit, overflow, _) = Run(["--all", "--db", db, "--partition-plan"], tooMany);
        using (overflow)
        {
            Assert.Equal(CommandExitCodes.PartialResult, overflowExit);
            Assert.False(overflow.RootElement.GetProperty("available").GetBoolean());
            Assert.Equal("plan_query_budget", overflow.RootElement.GetProperty("reason").GetString());
            Assert.False(overflow.RootElement.TryGetProperty("units", out _));
        }
    }
}
