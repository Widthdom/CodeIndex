using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerIssue5187Tests
{
    [Fact]
    public void InspectSelectors_ScopeEveryDirectGraphCommandAndExposeBareNameAmbiguity_Issue5187()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_selector_5187");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            var selectors = GetInspectSelectors(dbPath);
            var alphaSelector = selectors["Issue5187Fixture.Issue5187Alpha.Issue5187Shared"];
            var betaSelector = selectors["Issue5187Fixture.Issue5187Beta.Issue5187Shared"];

            Assert.StartsWith("id:", alphaSelector, StringComparison.Ordinal);
            Assert.Contains("@g:", alphaSelector, StringComparison.Ordinal);

            AssertSelectedJsonRows(
                () => QueryCommandRunner.RunReferences(
                    ["--selector", alphaSelector, "--db", dbPath, "--json"],
                    QueryCommandTestSupport.JsonOptions),
                requiredText: "\"path\":\"src/Alpha.cs\"",
                forbiddenText: "\"path\":\"tests/Beta.cs\"");
            AssertSelectedJsonRows(
                () => QueryCommandRunner.RunCallers(
                    ["--selector", alphaSelector, "--db", dbPath, "--json"],
                    QueryCommandTestSupport.JsonOptions),
                requiredText: "InvokeAlpha",
                forbiddenText: "InvokeBeta");
            AssertSelectedJsonRows(
                () => QueryCommandRunner.RunCallees(
                    ["--selector", alphaSelector, "--db", dbPath, "--json"],
                    QueryCommandTestSupport.JsonOptions),
                requiredText: "Issue5187AlphaLeaf",
                forbiddenText: "Issue5187BetaLeaf");
            AssertSelectedJsonRows(
                () => QueryCommandRunner.RunImpact(
                    ["--selector", alphaSelector, "--db", dbPath, "--json", "--max-hops", "1"],
                    QueryCommandTestSupport.JsonOptions),
                requiredText: "InvokeAlpha",
                forbiddenText: "InvokeBeta");

            var (ambiguousExitCode, ambiguousStdout, ambiguousStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallees(
                    ["Issue5187Shared", "--db", dbPath, "--json", "--count", "--exact-name"],
                    QueryCommandTestSupport.JsonOptions));
            using var ambiguous = QueryCommandTestSupport.ParseJsonOutput(ambiguousStdout);

            Assert.Equal(CommandExitCodes.Success, ambiguousExitCode);
            Assert.Equal(string.Empty, ambiguousStderr);
            Assert.False(ambiguous.RootElement.GetProperty("identity_scoped").GetBoolean());
            Assert.Equal("ambiguous_name_union", ambiguous.RootElement.GetProperty("identity_scope_reason").GetString());
            Assert.Equal(2, ambiguous.RootElement.GetProperty("candidate_count").GetInt32());
            Assert.All(
                ambiguous.RootElement.GetProperty("candidates").EnumerateArray(),
                candidate => Assert.Contains("@g:", candidate.GetProperty("selector").GetString(), StringComparison.Ordinal));

            var (substringExitCode, substringStdout, substringStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallers(
                    ["Issue5187Shared", "--db", dbPath, "--json", "--count"],
                    QueryCommandTestSupport.JsonOptions));
            using var substring = QueryCommandTestSupport.ParseJsonOutput(substringStdout);

            Assert.Equal(CommandExitCodes.Success, substringExitCode);
            Assert.Equal(string.Empty, substringStderr);
            Assert.Equal(3, substring.RootElement.GetProperty("candidate_count").GetInt32());
            Assert.Contains(
                substring.RootElement.GetProperty("candidates").EnumerateArray(),
                candidate => candidate.GetProperty("qualified_name").GetString() ==
                    "Issue5187Fixture.Issue5187Alpha.Issue5187SharedSuffix");

            var (pathExitCode, pathStdout, pathStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunReferences(
                    ["Issue5187Shared", "--db", dbPath, "--json", "--count", "--exact-name", "--path", "tests/**"],
                    QueryCommandTestSupport.JsonOptions));
            using var pathScoped = QueryCommandTestSupport.ParseJsonOutput(pathStdout);

            Assert.Equal(CommandExitCodes.Success, pathExitCode);
            Assert.Equal(string.Empty, pathStderr);
            Assert.Equal(2, pathScoped.RootElement.GetProperty("candidate_count").GetInt32());
            Assert.Contains(
                pathScoped.RootElement.GetProperty("candidates").EnumerateArray(),
                candidate => candidate.GetProperty("qualified_name").GetString() ==
                    "Issue5187Fixture.Issue5187Alpha.Issue5187Shared");
            Assert.Contains(
                pathScoped.RootElement.GetProperty("candidates").EnumerateArray(),
                candidate => candidate.GetProperty("qualified_name").GetString() ==
                    "Issue5187Fixture.Issue5187Beta.Issue5187Shared");

            var (humanExitCode, humanStdout, humanStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallees(
                    ["Issue5187Shared", "--db", dbPath, "--exact-name"],
                    QueryCommandTestSupport.JsonOptions));

            Assert.Equal(CommandExitCodes.Success, humanExitCode);
            Assert.Contains("Issue5187AlphaLeaf", humanStdout, StringComparison.Ordinal);
            Assert.Contains("Issue5187BetaLeaf", humanStdout, StringComparison.Ordinal);
            Assert.Contains("not identity-scoped", humanStderr, StringComparison.Ordinal);
            Assert.Contains("Issue5187Fixture.Issue5187Alpha.Issue5187Shared", humanStderr, StringComparison.Ordinal);
            Assert.Contains("Issue5187Fixture.Issue5187Beta.Issue5187Shared", humanStderr, StringComparison.Ordinal);

            var (filteredExitCode, filteredStdout, filteredStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallers(
                    ["--selector", alphaSelector, "--db", dbPath, "--json", "--count", "--lang", "typescript"],
                    QueryCommandTestSupport.JsonOptions));
            using var filtered = QueryCommandTestSupport.ParseJsonOutput(filteredStdout);

            Assert.Equal(CommandExitCodes.Success, filteredExitCode);
            Assert.Equal(string.Empty, filteredStderr);
            Assert.Equal(0, filtered.RootElement.GetProperty("count").GetInt32());
            Assert.True(filtered.RootElement.GetProperty("identity_scoped").GetBoolean());
            Assert.Equal(alphaSelector, filtered.RootElement.GetProperty("selected_symbol").GetProperty("selector").GetString());

            AssertSelectedCountZero(dbPath, alphaSelector, "--path", "src/does-not-match.cs");
            AssertSelectedCountZero(dbPath, betaSelector, "--exclude-tests");

            var generatedSelector = GetInspectSelectors(
                dbPath,
                query: "Issue5187GeneratedRoot",
                includeGenerated: true)["Issue5187Fixture.Issue5187Generated.Issue5187GeneratedRoot"];
            AssertSelectedCountZero(dbPath, generatedSelector);
            AssertSelectedJsonRows(
                () => QueryCommandRunner.RunCallees(
                    ["--selector", generatedSelector, "--db", dbPath, "--json", "--include-generated"],
                    QueryCommandTestSupport.JsonOptions),
                requiredText: "Issue5187GeneratedLeaf",
                forbiddenText: "Issue5187AlphaLeaf");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphSelectors_RejectMissingMalformedNegativeStaleNotFoundAndCrossDatabaseValues_Issue5187()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_selector_errors_5187");
        var otherProjectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_selector_cross_db_5187");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            var selector = GetInspectSelectors(dbPath)["Issue5187Fixture.Issue5187Alpha.Issue5187Shared"];
            var generationSeparator = selector.IndexOf("@g:", StringComparison.Ordinal);
            var unversioned = selector[..generationSeparator];
            var generation = selector[(generationSeparator + 3)..];
            var staleGeneration = generation == "0000000000000000"
                ? "1111111111111111"
                : "0000000000000000";
            var symbolId = selector["id:".Length..generationSeparator];

            AssertSelectorError(
                dbPath,
                unversioned,
                CommandExitCodes.UsageError,
                "generation fingerprint");
            AssertSelectorError(
                dbPath,
                $"id:-1@g:{generation}",
                CommandExitCodes.UsageError,
                "invalid symbol selector");
            AssertSelectorError(
                dbPath,
                "not-a-selector",
                CommandExitCodes.UsageError,
                "invalid symbol selector");
            AssertSelectorError(
                dbPath,
                $"id:{symbolId}@g:{staleGeneration}",
                CommandExitCodes.NotFound,
                "stale");
            AssertSelectorError(
                dbPath,
                $"id:{long.MaxValue}@g:{generation}",
                CommandExitCodes.NotFound,
                "not found");

            var otherDbPath = CreateGraphFixture(otherProjectRoot);
            AssertSelectorError(
                otherDbPath,
                selector,
                CommandExitCodes.NotFound,
                "another database");

            var (missingExitCode, _, missingStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                QueryCommandRunner.RunCallers(
                    ["--selector", "", "--db", dbPath],
                    QueryCommandTestSupport.JsonOptions));
            Assert.Equal(CommandExitCodes.UsageError, missingExitCode);
            Assert.Contains("requires", missingStderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
            TestProjectHelper.DeleteDirectory(otherProjectRoot);
        }
    }

    [Fact]
    public void BoundedGraphCursor_IsBoundToTheSelectedSymbolIdentity_Issue5187()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_selector_cursor_5187");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            var selectors = GetInspectSelectors(dbPath);
            var alphaSelector = selectors["Issue5187Fixture.Issue5187Alpha.Issue5187Shared"];
            var betaSelector = selectors["Issue5187Fixture.Issue5187Beta.Issue5187Shared"];
            var firstArgs = new[]
            {
                "callers", "--selector", alphaSelector, "--db", dbPath, "--compact", "--limit", "1",
            };
            var (firstExitCode, firstStdout, firstStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                ProgramRunner.Run(firstArgs, QueryCommandTestSupport.JsonOptions, "1.0.0-test"));
            using var first = QueryCommandTestSupport.ParseJsonOutput(firstStdout);
            var cursor = first.RootElement.GetProperty("next_cursor").GetString();

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);

            var (mismatchExitCode, mismatchStdout, mismatchStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                ProgramRunner.Run(
                    ["callers", "--selector", betaSelector, "--db", dbPath, "--compact", "--limit", "1", "--cursor", cursor!],
                    QueryCommandTestSupport.JsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, mismatchExitCode);
            Assert.Equal(string.Empty, mismatchStdout);
            Assert.Contains("cursor", mismatchStderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cursor_mismatch", mismatchStderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphIdentityMetadata_SurvivesCompactEmptyAndByteBoundedOutput_Issue5187()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_selector_compact_5187");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            var selector = GetInspectSelectors(dbPath)["Issue5187Fixture.Issue5187Alpha.Issue5187Shared"];

            var (compactExitCode, compactStdout, compactStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                ProgramRunner.Run(
                    ["callers", "--selector", selector, "--db", dbPath, "--compact"],
                    QueryCommandTestSupport.JsonOptions,
                    "1.0.0-test"));
            using var compact = QueryCommandTestSupport.ParseJsonOutput(compactStdout);

            Assert.Equal(CommandExitCodes.Success, compactExitCode);
            Assert.Equal(string.Empty, compactStderr);
            Assert.True(compact.RootElement.GetProperty("identity_scoped").GetBoolean());
            Assert.Equal(selector, compact.RootElement.GetProperty("selected_symbol").GetProperty("selector").GetString());

            var (ambiguousExitCode, ambiguousStdout, ambiguousStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                ProgramRunner.Run(
                    ["callees", "Issue5187Shared", "--db", dbPath, "--compact", "--exact-name"],
                    QueryCommandTestSupport.JsonOptions,
                    "1.0.0-test"));
            using var ambiguous = QueryCommandTestSupport.ParseJsonOutput(ambiguousStdout);

            Assert.Equal(CommandExitCodes.Success, ambiguousExitCode);
            Assert.Equal(string.Empty, ambiguousStderr);
            Assert.False(ambiguous.RootElement.GetProperty("identity_scoped").GetBoolean());
            Assert.Equal(2, ambiguous.RootElement.GetProperty("candidate_count").GetInt32());

            var (emptyExitCode, emptyStdout, emptyStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                ProgramRunner.Run(
                    ["callers", "--selector", selector, "--db", dbPath, "--compact", "--path", "does-not-match/**"],
                    QueryCommandTestSupport.JsonOptions,
                    "1.0.0-test"));
            using var empty = QueryCommandTestSupport.ParseJsonOutput(emptyStdout);

            Assert.Equal(CommandExitCodes.Success, emptyExitCode);
            Assert.Equal(string.Empty, emptyStderr);
            Assert.Equal(0, empty.RootElement.GetProperty("count").GetInt32());
            Assert.True(empty.RootElement.GetProperty("identity_scoped").GetBoolean());
            Assert.Equal(selector, empty.RootElement.GetProperty("selected_symbol").GetProperty("selector").GetString());

            var (boundedExitCode, boundedStdout, boundedStderr) = QueryCommandTestSupport.CaptureConsole(() =>
                ProgramRunner.Run(
                    ["callers", "--selector", selector, "--db", dbPath, "--compact", "--max-json-bytes", "4096"],
                    QueryCommandTestSupport.JsonOptions,
                    "1.0.0-test"));
            using var bounded = QueryCommandTestSupport.ParseJsonOutput(boundedStdout);
            var responseContext = bounded.RootElement
                .GetProperty("metadata")
                .GetProperty("response_context");

            Assert.Equal(CommandExitCodes.Success, boundedExitCode);
            Assert.Equal(string.Empty, boundedStderr);
            Assert.True(responseContext.GetProperty("identity_scoped").GetBoolean());
            Assert.Equal(selector, responseContext.GetProperty("selected_symbol").GetProperty("selector").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void GraphSelector_AppearsInHelpStructuredFieldDiscoveryAndEveryShellCompletion_Issue5187()
    {
        foreach (var command in new[] { "references", "callers", "callees", "impact" })
        {
            Assert.Contains(
                CliFlagSchema.GetCompletionFlagsForCommand(command),
                flag => flag.Name == "--selector"
                        && flag.GetValuePlaceholder(command) == "<id:n@g:fingerprint>");
            Assert.Contains("--selector <id:n@g:fingerprint>", ConsoleUi.GetUsageLine(command), StringComparison.Ordinal);
        }

        foreach (var shell in new[] { "bash", "zsh", "fish", "powershell" })
            Assert.Contains("selector", ConsoleCompletionRenderer.GetCompletionScript(shell), StringComparison.Ordinal);

        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_graph_selector_fields_5187");
        try
        {
            var dbPath = CreateGraphFixture(projectRoot);
            foreach (var command in new[] { "references", "callers", "callees", "impact" })
            {
                var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
                    ProgramRunner.Run(
                        [command, "Issue5187Shared", "--db", dbPath, "--fields", "list"],
                        QueryCommandTestSupport.JsonOptions,
                        "1.0.0-test"));
                using var fields = QueryCommandTestSupport.ParseJsonOutput(stdout);
                var validFields = fields.RootElement.GetProperty("valid_fields")
                    .EnumerateArray()
                    .Select(field => field.GetString())
                    .ToArray();

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.Contains("identity_scoped", validFields);
                Assert.Contains("selected_symbol", validFields);
                Assert.Contains("candidates", validFields);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static void AssertSelectedJsonRows(Func<int> run, string requiredText, string forbiddenText)
    {
        var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(run);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(requiredText, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenText, stdout, StringComparison.Ordinal);
        Assert.Contains("\"identity_scoped\":true", stdout, StringComparison.Ordinal);
        Assert.Contains("\"identity_scope_reason\":\"selected_symbol_id\"", stdout, StringComparison.Ordinal);
        Assert.Contains("\"selected_symbol\"", stdout, StringComparison.Ordinal);
    }

    private static void AssertSelectorError(
        string dbPath,
        string selector,
        int expectedExitCode,
        string expectedMessage)
    {
        var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
            QueryCommandRunner.RunCallers(
                ["--selector", selector, "--db", dbPath, "--json", "--path", "does-not-match/**"],
                QueryCommandTestSupport.JsonOptions));
        using var error = QueryCommandTestSupport.ParseJsonOutput(stdout);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains(expectedMessage, error.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(error.RootElement.TryGetProperty("error_code", out _));
        Assert.True(error.RootElement.TryGetProperty("category", out _));
    }

    private static void AssertSelectedCountZero(
        string dbPath,
        string selector,
        params string[] filters)
    {
        var args = new List<string>
        {
            "--selector", selector, "--db", dbPath, "--json", "--count",
        };
        args.AddRange(filters);
        var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
            QueryCommandRunner.RunCallees(args.ToArray(), QueryCommandTestSupport.JsonOptions));
        using var result = QueryCommandTestSupport.ParseJsonOutput(stdout);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(0, result.RootElement.GetProperty("count").GetInt32());
        Assert.True(result.RootElement.GetProperty("identity_scoped").GetBoolean());
        Assert.Equal(selector, result.RootElement.GetProperty("selected_symbol").GetProperty("selector").GetString());
    }

    private static Dictionary<string, string> GetInspectSelectors(
        string dbPath,
        string query = "Issue5187Shared",
        bool includeGenerated = false)
    {
        var args = new List<string> { query, "--db", dbPath, "--json", "--limit", "10" };
        if (includeGenerated)
            args.Add("--include-generated");
        var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
            QueryCommandRunner.RunInspect(
                args.ToArray(),
                QueryCommandTestSupport.JsonOptions));
        using var inspect = QueryCommandTestSupport.ParseJsonOutput(stdout);

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        return inspect.RootElement.GetProperty("candidate_bundles")
            .EnumerateArray()
            .Select(bundle => bundle.GetProperty("selector"))
            .ToDictionary(
                selector => selector.GetProperty("qualified_name").GetString()!,
                selector => selector.GetProperty("selector").GetString()!,
                StringComparer.Ordinal);
    }

    internal static string CreateGraphFixture(string projectRoot)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        SeedGraphFixture(dbPath);
        return dbPath;
    }

    internal static void SeedGraphFixture(string dbPath)
    {
        TestProjectHelper.InsertIndexedFiles(
            dbPath,
            [
                new TestProjectHelper.IndexedFileFixture(
                    "src/Alpha.cs",
                    "csharp",
                    """
                    namespace Issue5187Fixture;

                    public static class Issue5187Alpha
                    {
                        public static void Issue5187Shared() => Issue5187AlphaLeaf();
                        public static void Issue5187SharedSuffix() => Issue5187SuffixLeaf();
                        private static void Issue5187AlphaLeaf() { }
                        private static void Issue5187SuffixLeaf() { }
                    }

                    public static class Issue5187AlphaCallerA
                    {
                        public static void InvokeAlphaA() => Issue5187Alpha.Issue5187Shared();
                    }

                    public static class Issue5187AlphaCallerB
                    {
                        public static void InvokeAlphaB() => Issue5187Alpha.Issue5187Shared();
                        public static void InvokeSuffix() => Issue5187Alpha.Issue5187SharedSuffix();
                    }
                    """),
                new TestProjectHelper.IndexedFileFixture(
                    "tests/AlphaConsumer.cs",
                    "csharp",
                    """
                    namespace Issue5187Fixture;

                    public static class Issue5187AlphaTestCaller
                    {
                        public static void InvokeAlphaFromTests() => Issue5187Alpha.Issue5187Shared();
                    }
                    """),
                new TestProjectHelper.IndexedFileFixture(
                    "tests/Beta.cs",
                    "csharp",
                    """
                    namespace Issue5187Fixture;

                    public static class Issue5187Beta
                    {
                        public static void Issue5187Shared() => Issue5187BetaLeaf();
                        private static void Issue5187BetaLeaf() { }
                    }

                    public static class Issue5187BetaCaller
                    {
                        public static void InvokeBeta() => Issue5187Beta.Issue5187Shared();
                    }
                    """),
                new TestProjectHelper.IndexedFileFixture(
                    "src/Generated.g.cs",
                    "csharp",
                    """
                    namespace Issue5187Fixture;

                    public static class Issue5187Generated
                    {
                        public static void Issue5187GeneratedRoot() => Issue5187GeneratedLeaf();
                        private static void Issue5187GeneratedLeaf() { }
                    }
                    """,
                    IsGenerated: true),
            ]);

        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        var writer = new DbWriter(db.Connection);
        writer.MarkGraphReady();
        writer.MarkReferenceIdentityContractReady();
        writer.StampSymbolExtractorVersions(["csharp"]);
    }
}
