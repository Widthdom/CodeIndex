using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using Xunit;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerRecipeTokenBoundaryIssue5298Tests
{
    [Fact]
    public void Recipe_DefaultAndOverridesPreservePositiveMembersCountsAndOrigins()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("recipe_boundary_5298");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        string[] positives = ["info.ArgumentList.Add(value);", "info?.ArgumentList.Add(value);",
            "info.@ArgumentList.Add(value);", "引数.ArgumentList.Add(value);"];
        for (var i = 0; i < positives.Length; i++)
            TestProjectHelper.InsertIndexedFile(db, $"src/Positive{i}.cs", "csharp", positives[i] + "\n");
        TestProjectHelper.InsertIndexedFile(db, "src/Long.cs", "csharp", "JvmMethodReferenceTypeArgumentListPattern();\nTrySkipJsonTrustGenericArgumentList();\n");
        TestProjectHelper.InsertIndexedFile(db, "src/Unicode.cs", "csharp", "日本ArgumentList();\nArgumentList日本();\n");
        TestProjectHelper.InsertIndexedFile(db, "src/Comment.cs", "csharp", "// info.ArgumentList.Add(value);\n");
        TestProjectHelper.InsertIndexedFile(db, "src/String.cs", "csharp", "var text = \"info.ArgumentList.Add(value)\";\n");
        // The existing exact matcher normalizes @ identifiers, but does not decode Unicode escapes.
        TestProjectHelper.InsertIndexedFile(db, "src/Escape.cs", "csharp", "info.\\u0041rgumentList.Add(value);\n");
        TestProjectHelper.InsertIndexedFile(db, "src/Mixed.cs", "csharp", "TypeArgumentListPattern(); // ArgumentList\n");
        TestProjectHelper.InsertIndexedFile(db, "src/MixedString.cs", "csharp", "TypeArgumentListPattern(\"ArgumentList\");\n");
        string[] common = ["dogfood-risk-patterns/process-argument-list", "--db", db, "--json", "--limit", "100"];
        foreach (var flags in new string[][] { [], ["--token-boundary"], ["--reject-before", "NeverPresent5298", "--guard-scope", "same-line"] })
        {
            var (exit, output, _) = CaptureConsole(() => QueryCommandRunner.RunAudit([.. common, .. flags], JsonOptions));
            Assert.Equal(0, exit);
            using var document = JsonDocument.Parse(output);
            var query = document.RootElement.GetProperty("queries")[0];
            Assert.True(query.GetProperty("token_boundary").GetBoolean());
            var paths = query.GetProperty("results").EnumerateArray().Select(row => row.GetProperty("path").GetString()).Order().ToArray();
            Assert.Equal(Enumerable.Range(0, positives.Length).Select(i => $"src/Positive{i}.cs").ToArray(), paths);
            var (countExit, counts, _) = CaptureConsole(() => QueryCommandRunner.RunAudit([.. common, .. flags, "--count"], JsonOptions));
            Assert.Equal(0, countExit);
            using var countDocument = JsonDocument.Parse(counts);
            Assert.Equal(positives.Length, countDocument.RootElement.GetProperty("result_count").GetInt32());
        }
        var (substringExit, substring, _) = CaptureConsole(() => QueryCommandRunner.RunAudit([.. common, "--exact-substring"], JsonOptions));
        Assert.Equal(0, substringExit);
        using var substringDocument = JsonDocument.Parse(substring);
        var substringQuery = substringDocument.RootElement.GetProperty("queries")[0];
        Assert.False(substringQuery.GetProperty("token_boundary").GetBoolean());
        Assert.Contains(substringQuery.GetProperty("results").EnumerateArray(), row => row.GetProperty("path").GetString() == "src/Long.cs");
        Assert.Contains(substringQuery.GetProperty("results").EnumerateArray(), row => row.GetProperty("path").GetString() == "src/Unicode.cs");

        var (_, firstPage, _) = CaptureConsole(() => QueryCommandRunner.RunAudit([.. common, "--limit", "1"], JsonOptions));
        var cursor = JsonNode.Parse(firstPage)!["queries"]![0]!["next_cursor"]!.GetValue<string>();
        Assert.StartsWith("recipe:v2:", cursor);
        var (resumeExit, resumed, _) = CaptureConsole(() => QueryCommandRunner.RunAudit([.. common, "--limit", "1", "--cursor", cursor], JsonOptions));
        Assert.Equal(0, resumeExit);
        Assert.NotEqual(JsonNode.Parse(firstPage)!["queries"]![0]!["results"]![0]!["path"]!.GetValue<string>(),
            JsonNode.Parse(resumed)!["queries"]![0]!["results"]![0]!["path"]!.GetValue<string>());
        foreach (var invalid in new string[][] { ["--exact-substring", "--cursor", cursor], ["--cursor", "0:1:1"] })
        {
            var (exit, output, _) = CaptureConsole(() => QueryCommandRunner.RunAudit([.. common, "--limit", "1", .. invalid], JsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, exit);
            Assert.Contains("mismatched recipe cursor", output);
        }

        var input = JsonSerializer.Serialize(new string[] { "audit", common[0], "--token-boundary", "--count", "--json" }) + "\n";
        var (batchExit, batch, _) = CaptureConsoleWithInput(input, () => QueryCommandRunner.RunBatch(
            ["--db", db, "--json-summary"], JsonOptions));
        Assert.Equal(0, batchExit);
        Assert.Contains("\"exit_code\":0", batch);
    }

    [Fact]
    public void ExternalRecipe_SubstringDefaultAndBoundaryConfigurationShareExecutionAndCompatibility()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("recipe_external_boundary_5298");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "src/Exact.cs", "csharp", "info.ArgumentList.Add(value);\n");
        TestProjectHelper.InsertIndexedFile(db, "src/Long.cs", "csharp", "TypeArgumentListPattern();\n");
        var recipePath = Path.Combine(project.Root, "recipes.json");
        File.WriteAllText(recipePath, """{"recipes":[{"name":"boundary5298","description":"fixture","queries":[{"name":"substring","query":"ArgumentList","description":"substring"},{"name":"boundary","query":"ArgumentList","description":"boundary","token_boundary":true}]}]}""");
        var previous = Environment.GetEnvironmentVariable(SearchAuditRecipes.RecipePathsEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);
            string[] common = ["--recipe", "boundary5298", "--db", db, "--json"];
            foreach (var (flags, expected) in new (string[], int[])[] { ([], [2, 1]), (["--token-boundary"], [1, 1]), (["--exact-substring"], [2, 2]) })
            {
                var (exit, output, _) = CaptureConsole(() => QueryCommandRunner.RunSearch([.. common, .. flags, "--count"], JsonOptions));
                Assert.Equal(0, exit);
                using var document = JsonDocument.Parse(output);
                Assert.Equal(expected, document.RootElement.GetProperty("queries").EnumerateArray().Select(q => q.GetProperty("count").GetInt32()).ToArray());
            }
            var registry = SearchAuditRecipes.Load();
            var fixture = registry.Recipes.Single(r => r.Name == "boundary5298");
            string[] all = ["--all", "--db", db, "--json", "--limit", "1", "--total-limit", "1"];
            var (_, first, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(all, JsonOptions, [fixture]));
            var token = JsonNode.Parse(first)!["continuation"]!["next_token"]!.GetValue<string>();
            foreach (var (flags, recipe) in new (string[], SearchAuditRecipe)[]
            {
                (["--token-boundary"], fixture),
                ([], fixture with { Queries = [fixture.Queries[0] with { TokenBoundary = true }, fixture.Queries[1]] }),
            })
            {
                var calls = 0;
                var (exit, _, _) = CaptureConsole(() => QueryCommandRunner.RunAuditAllForTesting(
                    [.. all, .. flags, "--continuation", token], JsonOptions, [recipe], beforeQueryForTesting: _ => calls++));
                Assert.Equal(CommandExitCodes.UsageError, exit);
                Assert.Equal(0, calls);
            }
            File.WriteAllText(recipePath, """{"recipes":[{"name":"invalid5298","description":"fixture","queries":[{"name":"invalid","query":"ArgumentList","description":"fixture","tokenBoundary":"yes"}]}]}""");
            var invalidRegistry = SearchAuditRecipes.Load();
            Assert.DoesNotContain(invalidRegistry.Recipes, r => r.Name == "invalid5298");
            Assert.Contains(invalidRegistry.Diagnostics, d => d.Contains("tokenBoundary must be a boolean", StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable(SearchAuditRecipes.RecipePathsEnvironmentVariable, previous); }
    }

    [Fact]
    public void Baseline_ChangedBoundaryDefinitionCannotResolvePriorObservations()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("recipe_baseline_boundary_5298");
        File.WriteAllText(Path.Combine(project.Root, "One.cs"), "class One { void Run() { TypeArgumentListPattern(); } }\n");
        var db = Path.Combine(project.Root, ".cdidx", "codeindex.db");
        var baseline = Path.Combine(project.Root, ".cdidx", "baseline.json");
        var (indexed, _, _) = CaptureConsole(() => IndexCommandRunner.Run([project.Root, "--db", db, "--json"], JsonOptions));
        Assert.Equal(0, indexed);
        var query = new SearchAuditRecipeQuery("arguments", "ArgumentList", "fixture", [], "Review.");
        SearchAuditRecipeRegistry Registry(bool boundary) => new([new SearchAuditRecipe("fixture5298", "fixture",
            [query with { TokenBoundary = boundary }])], []);
        var (exported, _, _) = CaptureConsole(() => QueryCommandRunner.RunAuditBaseline(
            ["export", baseline, "--db", db, "--json"], JsonOptions, registryForTesting: Registry(false)));
        Assert.Equal(0, exported);
        var (compared, output, _) = CaptureConsole(() => QueryCommandRunner.RunAuditBaseline(
            ["compare", baseline, "--db", db, "--json"], JsonOptions, registryForTesting: Registry(true)));
        Assert.Equal(CommandExitCodes.PartialResult, compared);
        var result = JsonNode.Parse(output)!;
        Assert.False(result["comparable"]!.GetValue<bool>());
        Assert.Equal(0, result["totals"]!["resolved"]!.GetValue<int>());
        Assert.Equal(1, result["totals"]!["unknown"]!.GetValue<int>());
    }
}
