using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class AuditScopeIssue5281Tests
{
    [Fact]
    public void ProductionAndToolingScopeIncludesAutomationAndReportsBoundedCoverage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_audit_scope_5281");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        foreach (var (path, lang) in new[]
        {
            ("src/App.cs", "csharp"),
            ("tools/release.sh", "shell"),
            ("installers/modules/selftest.cs", "csharp"),
            (".github/workflows/build.yml", "yaml"),
            (".agent_harness/audit.py", "python"),
            ("custom/runtime.cs", "csharp"),
            ("custom/nested/tests/Smoke.cs", "csharp"),
            ("custom/fixtures/sample.py", "python"),
            ("docs/guide.md", "markdown"),
            ("custom/guide.md", "markdown"),
            ("src/CodeIndex/Cli/SearchAuditRecipes.cs", "csharp"),
        })
        {
            TestProjectHelper.InsertIndexedFile(dbPath, path, lang, "ProcessStartInfo");
        }
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "generated/Bindings.g.cs",
            "csharp",
            "ProcessStartInfo",
            isGenerated: true);
        SetUnknownExtensionInventory(
            dbPath,
            3,
            ["tools/bootstrap.weird", "docs/archive.weird", "custom/tests/missing.weird"],
            truncated: false);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/process-start-info", "--db", dbPath, "--json", "--limit", "20", "--audit-scope", "production-and-tooling", "--show-excluded"],
            JsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var scope = document.RootElement.GetProperty("scope");
        var coverage = scope.GetProperty("coverage");
        var paths = document.RootElement.GetProperty("queries")[0].GetProperty("results")
            .EnumerateArray()
            .Select(result => result.GetProperty("path").GetString())
            .ToList();

        Assert.Equal("production-and-tooling", scope.GetProperty("name").GetString());
        Assert.Empty(scope.GetProperty("path_patterns").EnumerateArray());
        Assert.True(scope.GetProperty("exclude_tests").GetBoolean());
        Assert.Contains("tools/release.sh", paths);
        Assert.Contains("installers/modules/selftest.cs", paths);
        Assert.Contains(".github/workflows/build.yml", paths);
        Assert.Contains(".agent_harness/audit.py", paths);
        Assert.Contains("custom/runtime.cs", paths);
        Assert.DoesNotContain("custom/nested/tests/Smoke.cs", paths);
        Assert.DoesNotContain("custom/fixtures/sample.py", paths);
        Assert.DoesNotContain("docs/guide.md", paths);
        Assert.DoesNotContain("custom/guide.md", paths);
        Assert.DoesNotContain("src/CodeIndex/Cli/SearchAuditRecipes.cs", paths);
        Assert.DoesNotContain("generated/Bindings.g.cs", paths);

        AssertCoverageSet(coverage.GetProperty("included"), 6, authoritative: true);
        AssertCoverageSet(coverage.GetProperty("excluded"), 6, authoritative: true);
        AssertCoverageSet(coverage.GetProperty("unindexed"), 1, authoritative: true);
        Assert.Contains(
            coverage.GetProperty("unindexed").GetProperty("paths").EnumerateArray(),
            path => path.GetString() == "tools/bootstrap.weird");
        Assert.Equal(0, coverage.GetProperty("unexecuted").GetProperty("count").GetInt32());
        Assert.True(coverage.GetProperty("execution_complete").GetBoolean());
        Assert.Equal("not_declared", coverage.GetProperty("human_review").GetProperty("state").GetString());
        Assert.False(coverage.GetProperty("human_review").GetProperty("explicit_annotation_present").GetBoolean());

        var ndjson = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/process-start-info", "--db", dbPath, "--json=ndjson", "--limit", "1", "--audit-scope", "production-and-tooling"],
            JsonOptions));
        Assert.Equal(CommandExitCodes.Success, ndjson.Result);
        using var terminal = JsonDocument.Parse(ndjson.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.Equal(
            "production-and-tooling",
            terminal.RootElement.GetProperty("scope").GetProperty("name").GetString());
        Assert.Equal(
            0,
            terminal.RootElement.GetProperty("scope").GetProperty("coverage").GetProperty("unexecuted").GetProperty("count").GetInt32());

        var boundedNdjson = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/process-start-info", "--db", dbPath, "--json=ndjson", "--limit", "1", "--audit-scope", "production-and-tooling", "--max-json-bytes", "1000000"],
            JsonOptions));
        Assert.Equal(CommandExitCodes.Success, boundedNdjson.Result);
        using var boundedTerminal = JsonDocument.Parse(boundedNdjson.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        var boundedCoverage = boundedTerminal.RootElement.GetProperty("scope").GetProperty("coverage");
        Assert.Contains(
            boundedCoverage.GetProperty("included").GetProperty("paths").EnumerateArray(),
            path => path.GetString() == ".agent_harness/audit.py");
        Assert.False(boundedCoverage.GetProperty("included").TryGetProperty("path_sample_omitted_reason", out _));

        var countSummary = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/process-start-info", "--db", dbPath, "--json", "--count", "--summary-only", "--audit-scope", "production-and-tooling"],
            JsonOptions));
        Assert.Equal(CommandExitCodes.Success, countSummary.Result);
        using var countDocument = JsonDocument.Parse(countSummary.Stdout);
        AssertCoverageSet(countDocument.RootElement.GetProperty("coverage").GetProperty("included"), 6, authoritative: true);

        var includeGenerated = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/process-start-info", "--db", dbPath, "--json", "--limit", "20", "--audit-scope", "production-and-tooling", "--include-generated"],
            JsonOptions));
        Assert.Equal(CommandExitCodes.Success, includeGenerated.Result);
        using var generatedDocument = JsonDocument.Parse(includeGenerated.Stdout);
        var generatedScope = generatedDocument.RootElement.GetProperty("scope");
        Assert.Equal("include", generatedScope.GetProperty("coverage").GetProperty("generated_code_policy").GetString());
        AssertCoverageSet(generatedScope.GetProperty("coverage").GetProperty("included"), 7, authoritative: true);
        AssertCoverageSet(generatedScope.GetProperty("coverage").GetProperty("excluded"), 5, authoritative: true);
        Assert.Contains(
            generatedDocument.RootElement.GetProperty("queries")[0].GetProperty("results").EnumerateArray(),
            result => result.GetProperty("path").GetString() == "generated/Bindings.g.cs");
    }

    [Fact]
    public void ProductionAndToolingCoveragePreservesCustomFiltersAndStatesUnknownBounds()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_audit_scope_bounds_5281");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "tools/release.sh", "shell", "ProcessStartInfo");
        TestProjectHelper.InsertIndexedFile(dbPath, "tools/private/secret.sh", "shell", "ProcessStartInfo");
        TestProjectHelper.InsertIndexedFile(dbPath, ".github/workflows/build.yml", "yaml", "ProcessStartInfo");
        SetUnknownExtensionInventory(
            dbPath,
            7,
            ["tools/bootstrap.weird", "docs/archive.weird", "custom/tests/missing.weird"],
            truncated: true);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/process-start-info", "--db", dbPath, "--json", "--limit", "20", "--audit-scope", "production-and-tooling", "--path", "tools/**", "--exclude-path", "tools/private/**"],
            JsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        var scope = document.RootElement.GetProperty("scope");
        var coverage = scope.GetProperty("coverage");
        var result = Assert.Single(document.RootElement.GetProperty("queries")[0].GetProperty("results").EnumerateArray());

        Assert.Equal("tools/release.sh", result.GetProperty("path").GetString());
        Assert.Contains(scope.GetProperty("path_patterns").EnumerateArray(), path => path.GetString() == "tools/**");
        Assert.Contains(scope.GetProperty("exclude_paths").EnumerateArray(), path => path.GetString() == "tools/private/**");
        AssertCoverageSet(coverage.GetProperty("included"), 1, authoritative: true);
        AssertCoverageSet(coverage.GetProperty("excluded"), 2, authoritative: true);

        var unindexed = coverage.GetProperty("unindexed");
        Assert.False(unindexed.TryGetProperty("count", out _));
        Assert.False(unindexed.GetProperty("count_authoritative").GetBoolean());
        Assert.Equal(1, unindexed.GetProperty("count_lower_bound").GetInt64());
        Assert.Equal(5, unindexed.GetProperty("count_upper_bound").GetInt64());
        Assert.True(unindexed.GetProperty("paths_truncated").GetBoolean());
        Assert.False(unindexed.TryGetProperty("omitted_path_count", out _));
        Assert.False(unindexed.GetProperty("omitted_path_count_authoritative").GetBoolean());
        Assert.Equal(
            "unknown_extension_path_inventory_truncated_before_scope_filtering",
            unindexed.GetProperty("uncertainty_reason").GetString());

        var languageFiltered = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/process-start-info", "--db", dbPath, "--json", "--limit", "20", "--audit-scope", "production-and-tooling", "--path", "tools/**", "--lang", "shell"],
            JsonOptions));
        Assert.Equal(CommandExitCodes.Success, languageFiltered.Result);
        using var languageDocument = JsonDocument.Parse(languageFiltered.Stdout);
        var languageUnindexed = languageDocument.RootElement
            .GetProperty("scope")
            .GetProperty("coverage")
            .GetProperty("unindexed");
        Assert.False(languageUnindexed.GetProperty("count_authoritative").GetBoolean());
        Assert.Equal(
            "unknown_extension_inventory_has_no_language_classification_for_lang_filter",
            languageUnindexed.GetProperty("uncertainty_reason").GetString());

        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = """
                DROP INDEX idx_files_generated;
                ALTER TABLE files DROP COLUMN generated;
                """;
            command.ExecuteNonQuery();
        }

        var legacy = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "risky-code/process-start-info", "--db", dbPath, "--json", "--limit", "20", "--audit-scope", "production-and-tooling"],
            JsonOptions));
        Assert.Equal(CommandExitCodes.Success, legacy.Result);
        using var legacyDocument = JsonDocument.Parse(legacy.Stdout);
        Assert.Equal(
            "unavailable",
            legacyDocument.RootElement
                .GetProperty("scope")
                .GetProperty("coverage")
                .GetProperty("generated_code_policy")
                .GetString());
    }

    [Fact]
    public void ExternalToolingDefaultPreservesChildPathAndExcludeFilters()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_external_audit_scope_5281");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        var recipePath = Path.Combine(project.Root, "external-recipes.json");
        File.WriteAllText(
            recipePath,
            """
            [
              {
                "name": "tooling-default",
                "description": "Exercise the configured production-and-tooling default.",
                "default_scope": "production-and-tooling",
                "queries": [
                  {
                    "name": "source-child",
                    "query": "BoundaryNeedle",
                    "description": "Keep this child query source-scoped.",
                    "path_patterns": ["src/**"],
                    "exclude_paths": ["src/private/**"]
                  }
                ]
              }
            ]
            """);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/public.cs", "csharp", "BoundaryNeedle");
        TestProjectHelper.InsertIndexedFile(dbPath, "src/private/secret.cs", "csharp", "BoundaryNeedle");
        TestProjectHelper.InsertIndexedFile(dbPath, "tools/release.sh", "shell", "BoundaryNeedle");

        using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
        env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, recipePath);
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
            ["--recipe", "tooling-default", "--db", dbPath, "--json", "--limit", "20"],
            JsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("production-and-tooling", document.RootElement.GetProperty("scope").GetProperty("name").GetString());
        var query = Assert.Single(document.RootElement.GetProperty("queries").EnumerateArray());
        Assert.Contains(query.GetProperty("path_patterns").EnumerateArray(), path => path.GetString() == "src/**");
        Assert.Contains(query.GetProperty("exclude_paths").EnumerateArray(), path => path.GetString() == "src/private/**");
        var result = Assert.Single(query.GetProperty("results").EnumerateArray());
        Assert.Equal("src/public.cs", result.GetProperty("path").GetString());
    }

    private static void SetUnknownExtensionInventory(
        string dbPath,
        int count,
        string[] paths,
        bool truncated)
    {
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.SetMeta(DbContext.UnknownExtensionDiagnosticsVersionMetaKey, DbContext.UnknownExtensionDiagnosticsVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.UnknownExtensionFileCountMetaKey, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.SetMeta(DbContext.UnknownExtensionFilePathsMetaKey, JsonSerializer.Serialize(paths));
        writer.SetMeta(DbContext.UnknownExtensionFilesTruncatedMetaKey, truncated.ToString());
        writer.SetMeta(DbContext.UnknownExtensionFilePathLimitMetaKey, paths.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AssertCoverageSet(JsonElement coverage, long count, bool authoritative)
    {
        Assert.Equal(count, coverage.GetProperty("count").GetInt64());
        Assert.Equal(authoritative, coverage.GetProperty("count_authoritative").GetBoolean());
        Assert.Equal(count, coverage.GetProperty("count_lower_bound").GetInt64());
        Assert.Equal(count, coverage.GetProperty("count_upper_bound").GetInt64());
        Assert.True(coverage.GetProperty("omitted_path_count_authoritative").GetBoolean());
    }
}
