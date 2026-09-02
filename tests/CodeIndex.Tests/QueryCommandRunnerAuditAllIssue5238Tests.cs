using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using Xunit;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerAuditAllIssue5238Tests
{
    [Fact]
    public void AuditAll_UsesAuthoritativeRegistryNamesInOrdinalOrder_Issue5238()
    {
        var registry = SearchAuditRecipes.Load();
        var expectedNames = registry.Recipes
            .Select(recipe => recipe.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAudit(
            ["--all", "--json"],
            JsonOptions,
            cancellation.Token));
        using var document = JsonDocument.Parse(stdout);

        Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
        Assert.Equal(expectedNames, document.RootElement.GetProperty("selected_recipe_names").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(expectedNames.Length, document.RootElement.GetProperty("selected_recipe_count").GetInt32());
        Assert.Equal(expectedNames.Length, document.RootElement.GetProperty("summary").GetProperty("omitted_recipe_count").GetInt32());
    }

    [Fact]
    public void AuditAll_PreservesOverlapAttributionAndSharedFilters_Issue5238()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_audit_all_overlap_5238");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp", "class One { string Value = \"Issue5238Needle\"; }\n");
        TestProjectHelper.InsertIndexedFile(dbPath, "tests/OneTests.cs", "csharp", "class OneTests { string Value = \"Issue5238Needle\"; }\n");
        var recipes = new[]
        {
            Recipe("z-second", "second-query", "Issue5238Needle"),
            Recipe("a-first", "first-query", "Issue5238Needle"),
        };

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            [
                "--all", "--db", dbPath, "--json", "--limit", "10", "--total-limit", "10",
                "--lang", "csharp", "--path", "src/**", "--exclude-tests",
            ],
            JsonOptions,
            recipes));
        using var document = JsonDocument.Parse(stdout);
        var recipeRuns = document.RootElement.GetProperty("recipes").EnumerateArray().ToArray();

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(["a-first", "z-second"], recipeRuns.Select(recipe => recipe.GetProperty("name").GetString()));
        Assert.All(recipeRuns, recipe => Assert.Equal(1, recipe.GetProperty("minimum_matched_result_count").GetInt32()));
        Assert.Equal(2, document.RootElement.GetProperty("summary").GetProperty("minimum_matched_result_count").GetInt32());
        Assert.Equal("sum_of_recipe_query_observations_not_unique_matches", document.RootElement.GetProperty("summary").GetProperty("count_semantics").GetString());
        foreach (var recipeRun in recipeRuns)
        {
            var row = recipeRun.GetProperty("queries")[0].GetProperty("results")[0];
            Assert.Equal("src/One.cs", row.GetProperty("path").GetString());
            Assert.Equal(recipeRun.GetProperty("name").GetString(), row.GetProperty("recipe").GetString());
            Assert.EndsWith("-query", row.GetProperty("query_name").GetString(), StringComparison.Ordinal);
        }

        var (summaryExitCode, summaryStdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", dbPath, "--summary-only", "--limit", "10"],
            JsonOptions,
            recipes));
        using var summary = JsonDocument.Parse(summaryStdout);
        Assert.Equal(CommandExitCodes.Success, summaryExitCode);
        Assert.Equal(2, summary.RootElement.GetProperty("summary").GetProperty("completed_recipe_count").GetInt32());
        Assert.All(
            summary.RootElement.GetProperty("recipes").EnumerateArray(),
            recipe => Assert.Equal(0, recipe.GetProperty("queries")[0].GetProperty("results").GetArrayLength()));

        var (ndjsonExitCode, ndjsonStdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", dbPath, "--json=ndjson", "--limit", "10", "--total-limit", "10"],
            JsonOptions,
            recipes));
        var ndjsonLines = ndjsonStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var firstRow = JsonDocument.Parse(ndjsonLines[0]);
        using var terminal = JsonDocument.Parse(ndjsonLines[^1]);
        Assert.Equal(CommandExitCodes.Success, ndjsonExitCode);
        Assert.Equal("a-first", firstRow.RootElement.GetProperty("recipe").GetString());
        Assert.True(terminal.RootElement.GetProperty("terminal_record").GetBoolean());
        Assert.Equal(2, terminal.RootElement.GetProperty("selected_recipe_count").GetInt32());
    }

    [Fact]
    public void AuditAll_TotalAndByteBudgetsOmitWholeRowsWithMetadata_Issue5238()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_audit_all_budgets_5238");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        foreach (var index in Enumerable.Range(1, 12))
        {
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/Many{index}.cs",
                "csharp",
                $"class Issue5238Needle{index} {{ }}\n");
        }
        var recipes = new[] { Recipe("budget-recipe", "budget-query", "Issue5238Needle") };

        var (limitedExitCode, limitedStdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", dbPath, "--json", "--limit", "10", "--total-limit", "1"],
            JsonOptions,
            recipes));
        using var limited = JsonDocument.Parse(limitedStdout);
        Assert.Equal(CommandExitCodes.Success, limitedExitCode);
        Assert.Equal(1, limited.RootElement.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
        Assert.True(limited.RootElement.GetProperty("summary").GetProperty("truncated").GetBoolean());

        var (_, fullStdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", dbPath, "--json", "--limit", "10", "--total-limit", "10"],
            JsonOptions,
            recipes));
        var fullBytes = Encoding.UTF8.GetByteCount(fullStdout);
        var byteLimit = fullBytes - 1;
        var (boundedExitCode, boundedStdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            [
                "--all", "--db", dbPath, "--json", "--limit", "10", "--total-limit", "10",
                "--max-json-bytes", byteLimit.ToString(),
            ],
            JsonOptions,
            recipes));
        using var bounded = JsonDocument.Parse(boundedStdout);

        Assert.Equal(CommandExitCodes.PartialResult, boundedExitCode);
        Assert.True(Encoding.UTF8.GetByteCount(boundedStdout) <= byteLimit);
        Assert.True(bounded.RootElement.GetProperty("limits").GetProperty("byte_omitted_result_count").GetInt32() > 0);
        Assert.True(bounded.RootElement.GetProperty("summary").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void AuditAll_QueryFailureDoesNotDiscardLaterRecipeSuccess_Issue5238()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_audit_all_failure_5238");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp", "class Issue5238Needle { }\n");
        var recipes = new[]
        {
            Recipe("z-success", "valid-query", "Issue5238Needle"),
            Recipe("a-failure", "invalid-query", new string('x', 100_000)),
        };

        var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", dbPath, "--format", "compact", "--limit", "2", "--allow-partial"],
            JsonOptions,
            recipes));
        using var document = JsonDocument.Parse(stdout);
        var recipeRuns = document.RootElement.GetProperty("recipes").EnumerateArray().ToArray();

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal("failed", recipeRuns[0].GetProperty("status").GetString());
        Assert.Equal("completed", recipeRuns[1].GetProperty("status").GetString());
        Assert.Equal(1, recipeRuns[1].GetProperty("minimum_matched_result_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("errors").GetProperty("count").GetInt32());
    }

    [Fact]
    public void AuditAll_TimeBudgetReturnsBoundedPartialSummary_Issue5238()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_audit_all_time_5238");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp", "class Issue5238Needle { }\n");
        QueryCommandRunner.AuditAllTimeBudgetForTesting = TimeSpan.Zero;
        try
        {
            var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
                ["--all", "--db", dbPath, "--format", "compact"],
                JsonOptions,
                [Recipe("time-recipe", "query", "Issue5238Needle")]));
            using var document = JsonDocument.Parse(stdout);

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.True(document.RootElement.GetProperty("summary").GetProperty("time_budget_exceeded").GetBoolean());
            Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("omitted_recipe_count").GetInt32());
        }
        finally
        {
            QueryCommandRunner.AuditAllTimeBudgetForTesting = null;
        }
    }

    [Fact]
    public void AuditAll_CancellationAfterCompletedQueryPreservesAccounting_Issue5238()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_audit_all_cancel_5238");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp", "class Issue5238Needle { }\n");
        using var cancellation = new CancellationTokenSource();
        var recipes = new[]
        {
            Recipe("a-completed", "first-query", "Issue5238Needle"),
            Recipe("z-omitted", "second-query", "Issue5238Needle"),
        };

        var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", dbPath, "--json", "--limit", "1"],
            JsonOptions,
            recipes,
            cancellation.Token,
            cancellation.Cancel));
        using var document = JsonDocument.Parse(stdout);

        Assert.Equal(CommandExitCodes.CancelledBySignal, exitCode);
        Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("completed_recipe_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("omitted_recipe_count").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
        Assert.True(document.RootElement.GetProperty("summary").GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public void AuditAll_RepresentativeLargeRegistryStaysWithinOutputAndAllocationBudgets_Issue5238()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_audit_all_scale_5238");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/One.cs", "csharp", "class Issue5238Needle { }\n");
        var recipes = Enumerable.Range(0, 128)
            .Select(index => Recipe($"recipe-{index:D3}", "query", "Issue5238Needle"))
            .ToArray();

        _ = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", dbPath, "--format", "compact", "--limit", "1"],
            JsonOptions,
            recipes));
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var (exitCode, stdout, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
            ["--all", "--db", dbPath, "--format", "compact", "--limit", "1"],
            JsonOptions,
            recipes));
        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        using var document = JsonDocument.Parse(stdout);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(128, document.RootElement.GetProperty("selected_recipe_count").GetInt32());
        Assert.Equal(128, document.RootElement.GetProperty("summary").GetProperty("emitted_result_count").GetInt32());
        Assert.Equal(QueryCommandRunner.DefaultAuditAllTotalLimit, document.RootElement.GetProperty("limits").GetProperty("effective_total_limit").GetInt32());
        Assert.True(document.RootElement.GetProperty("limits").GetProperty("total_limit_defaulted").GetBoolean());
        Assert.True(allocatedBytes < 32 * 1024 * 1024, $"Expected bounded allocation below 32 MiB, saw {allocatedBytes} bytes.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Expected bounded execution below 10 seconds, saw {stopwatch.Elapsed}.");
    }

    [Fact]
    public void AuditAll_MetadataAndHelpExposeAllMode_Issue5238()
    {
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("audit"), flag => flag.Name == "--all");
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("audit"), flag => flag.Name == "--allow-partial");
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("audit"), flag => flag.Name == "--path");
        var (printed, stdout, stderr) = ConsoleCapture.Capture(() => ConsoleUi.PrintCommandUsage("audit") ? 1 : 0);

        Assert.Equal(1, printed);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("<recipe|recipe/query>|--all", stdout, StringComparison.Ordinal);
        Assert.Contains("--total-limit <n>", stdout, StringComparison.Ordinal);
    }

    private static SearchAuditRecipe Recipe(string name, string queryName, string query)
        => new(
            name,
            $"Recipe {name}",
            [
                new SearchAuditRecipeQuery(
                    queryName,
                    query,
                    $"Query {queryName}",
                    [],
                    "Review the attributed match.",
                    ExactSubstring: true),
            ]);
}
