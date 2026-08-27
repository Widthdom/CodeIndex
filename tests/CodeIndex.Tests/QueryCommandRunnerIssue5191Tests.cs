using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerIssue5191Tests
{
    private const string RecipeName = "replay recipe $5191";
    private const string AlphaQueryName = "alpha query";
    private const string BetaQueryName = "beta '$query&";
    private const string ExcludedQueryName = "excluded; query";
    private const string PathFilter = "src/space $folder/**";
    private const string ExcludePathFilter = "src/space $folder/ignored &/**";

    [Fact]
    public void CompactAuditAndSearchNextCommands_ExecuteWithEquivalentState_Issue5191()
    {
        using var project = CreateFixture();
        using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
        env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, project.RecipePath);

        var commonArgs = new[]
        {
            "--db", project.DbPath,
            "--format", "compact",
            "--limit", "1",
            "--path", PathFilter,
            "--exclude-path", ExcludePathFilter,
            "--lang", "csharp",
            "--exclude-tests",
            "--include-query", AlphaQueryName,
            "--include-query", BetaQueryName,
            "--exclude-query", ExcludedQueryName,
        };
        using var audit = RunCompact(["audit", RecipeName, .. commonArgs]);
        using var search = RunCompact(["search", "--recipe", RecipeName, .. commonArgs]);

        Assert.Equal(GetQueryResultPaths(audit.RootElement), GetQueryResultPaths(search.RootElement));
        Assert.Equal(2, audit.RootElement.GetProperty("truncation").GetProperty("truncated_query_count").GetInt32());
        Assert.Equal(2, search.RootElement.GetProperty("truncation").GetProperty("truncated_query_count").GetInt32());

        AssertEveryNextCommandExecutes(
            audit.RootElement,
            BuildReplayOptions(["--recipe", RecipeName, .. commonArgs], QueryCommandInvocationContext.Audit),
            expectedDataOption: "--db");
        AssertEveryNextCommandExecutes(
            search.RootElement,
            BuildReplayOptions(["--recipe", RecipeName, .. commonArgs], QueryCommandInvocationContext.Search),
            expectedDataOption: "--db");
    }

    [Fact]
    public void CompactAuditSingleQueryReplay_PreservesDataDirAndExplicitByteBudget_Issue5191()
    {
        using var project = CreateFixture();
        using var env = EnvironmentVariableScope.Capture(SearchAuditRecipes.RecipePathsEnvironmentVariable);
        env.Set(SearchAuditRecipes.RecipePathsEnvironmentVariable, project.RecipePath);
        var selector = $"{RecipeName}/{AlphaQueryName}";
        var commonArgs = new[]
        {
            "--data-dir", project.DataDir,
            "--format", "compact",
            "--limit", "1",
            "--path", PathFilter,
            "--exclude-path", ExcludePathFilter,
            "--max-json-bytes", "1048576",
        };
        using var audit = RunCompact(["audit", selector, .. commonArgs]);

        Assert.Equal(1, audit.RootElement.GetProperty("truncation").GetProperty("truncated_query_count").GetInt32());
        AssertEveryNextCommandExecutes(
            audit.RootElement,
            BuildReplayOptions(["--recipe", selector, .. commonArgs], QueryCommandInvocationContext.Audit),
            expectedDataOption: "--data-dir");
    }

    private static JsonDocument RunCompact(string[] args)
    {
        var (exitCode, stdout, stderr) = QueryCommandTestSupport.CaptureConsole(() =>
            ProgramRunner.Run(args, QueryCommandTestSupport.JsonOptions, "1.0.0-test"));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        return QueryCommandTestSupport.ParseJsonOutput(stdout);
    }

    private static QueryCommandOptions BuildReplayOptions(
        string[] args,
        QueryCommandInvocationContext invocationContext)
    {
        var options = QueryCommandRunner.ParseArgs(
            args,
            jsonDefault: false,
            allowNamedQuery: true,
            allowIssueDraftsFormat: true,
            applySearchSourceDefaults: true);
        options.InvocationContext = invocationContext;
        Assert.Null(options.ParseError);
        return options;
    }

    private static void AssertEveryNextCommandExecutes(
        JsonElement root,
        QueryCommandOptions options,
        string expectedDataOption)
    {
        var queryStates = root.GetProperty("truncation").GetProperty("queries").EnumerateArray()
            .Select(query => new ReplayQueryState(
                query.GetProperty("name").GetString()!,
                query.GetProperty("next_cursor").GetString(),
                root.GetProperty("queries").EnumerateArray()
                    .Single(item => item.GetProperty("name").GetString() == query.GetProperty("name").GetString())
                    .GetProperty("results")[0]
                    .GetProperty("path")
                    .GetString()!))
            .ToList();
        var emittedCommands = root.GetProperty("next_commands").EnumerateArray()
            .Select(command => command.GetString()!)
            .ToList();
        var expectedReplays = queryStates
            .Where(query => query.Cursor is not null)
            .Take(3)
            .Select(query => (
                Query: (ReplayQueryState?)query,
                Replay: QueryCommandRunner.BuildSearchRecipeCompactReplayCommandForTests(
                    $"{RecipeName}/{query.Name}",
                    options,
                    query.Cursor,
                    resultsOnly: false,
                    includeRecipeQuerySelectors: false)))
            .ToList();
        var resultsOnlySelector = queryStates.Count == 1
            ? $"{RecipeName}/{queryStates[0].Name}"
            : RecipeName;
        expectedReplays.Add((
            Query: null,
            Replay: QueryCommandRunner.BuildSearchRecipeCompactReplayCommandForTests(
                resultsOnlySelector,
                options,
                cursor: null,
                resultsOnly: true,
                includeRecipeQuerySelectors: queryStates.Count != 1)));

        Assert.Equal(expectedReplays.Count, emittedCommands.Count);
        for (var index = 0; index < expectedReplays.Count; index++)
        {
            var (query, replay) = expectedReplays[index];
            Assert.Equal(replay.CurrentShell, emittedCommands[index]);
            Assert.Equal("cdidx", replay.Argv[0]);
            Assert.Contains(expectedDataOption, replay.Argv);
            Assert.Contains(PathFilter, replay.Argv);
            Assert.Contains(ExcludePathFilter, replay.Argv);
            Assert.Contains($"'{PathFilter}'", replay.PosixSh, StringComparison.Ordinal);
            Assert.Contains($"'{PathFilter}'", replay.PowerShell, StringComparison.Ordinal);
            var dataOptionIndex = replay.Argv.ToList().IndexOf(expectedDataOption);
            Assert.True(dataOptionIndex >= 0 && dataOptionIndex + 1 < replay.Argv.Count);
            var dataValue = replay.Argv[dataOptionIndex + 1];
            Assert.Contains($"'{dataValue}'", replay.PosixSh, StringComparison.Ordinal);
            Assert.Contains($"'{dataValue}'", replay.PowerShell, StringComparison.Ordinal);

            if (query is not null)
            {
                Assert.Contains(query.Cursor!, replay.Argv);
                Assert.Contains($"{RecipeName}/{query.Name}", replay.Argv);
                if (query.Name == BetaQueryName)
                {
                    Assert.Contains("beta '\\''$query&", replay.PosixSh, StringComparison.Ordinal);
                    Assert.Contains("beta ''$query&", replay.PowerShell, StringComparison.Ordinal);
                }
                AssertReplayReturnsNextPage(replay.Argv, query);
            }
            else
            {
                Assert.Equal("search", replay.Argv[1]);
                Assert.Equal("--recipe", replay.Argv[2]);
                Assert.Contains("--json=ndjson", replay.Argv);
                Assert.Contains("--results-only", replay.Argv);
                Assert.DoesNotContain("cdidx audit", replay.CurrentShell, StringComparison.OrdinalIgnoreCase);
                AssertResultsOnlyReplayReturnsRows(replay.Argv, queryStates);
            }
        }
    }

    private static void AssertReplayReturnsNextPage(
        IReadOnlyList<string> argv,
        ReplayQueryState query)
    {
        var (exitCode, stdout, stderr) = RunReplay(argv);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = QueryCommandTestSupport.ParseJsonOutput(stdout);
        var replayedQuery = Assert.Single(document.RootElement.GetProperty("queries").EnumerateArray());
        Assert.Equal(query.Name, replayedQuery.GetProperty("name").GetString());
        var replayedResult = Assert.Single(replayedQuery.GetProperty("results").EnumerateArray());
        Assert.NotEqual(query.FirstPagePath, replayedResult.GetProperty("path").GetString());
    }

    private static void AssertResultsOnlyReplayReturnsRows(
        IReadOnlyList<string> argv,
        IReadOnlyList<ReplayQueryState> queryStates)
    {
        var (exitCode, stdout, stderr) = RunReplay(argv);
        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        var rows = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line))
            .ToList();
        try
        {
            Assert.Equal(queryStates.Count, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.False(row.RootElement.TryGetProperty("terminal_record", out _));
                Assert.Equal(RecipeName, row.RootElement.GetProperty("recipe").GetString());
                Assert.Contains(
                    row.RootElement.GetProperty("query_name").GetString(),
                    queryStates.Select(query => query.Name));
            });
        }
        finally
        {
            foreach (var row in rows)
                row.Dispose();
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunReplay(IReadOnlyList<string> argv)
    {
        var executableArgs = argv.Skip(1)
            .Select(argument => argument == "<bytes>" ? "1048576" : argument)
            .ToArray();
        return QueryCommandTestSupport.CaptureConsole(() =>
            ProgramRunner.Run(executableArgs, QueryCommandTestSupport.JsonOptions, "1.0.0-test"));
    }

    private static List<string> GetQueryResultPaths(JsonElement root)
        => root.GetProperty("queries").EnumerateArray()
            .SelectMany(query => query.GetProperty("results").EnumerateArray())
            .Select(result => result.GetProperty("path").GetString()!)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static FixtureScope CreateFixture()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx issue 5191 $meta");
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        var recipePath = Path.Combine(projectRoot, "external recipes $5191.json");
        File.WriteAllText(
            recipePath,
            $$"""
            {
              "recipes": [
                {
                  "name": "{{RecipeName}}",
                  "description": "Exercise executable compact replay commands.",
                  "queries": [
                    {
                      "name": "{{AlphaQueryName}}",
                      "query": "AlphaNeedle5191",
                      "description": "Find alpha replay fixtures."
                    },
                    {
                      "name": "beta '$query&",
                      "query": "BetaNeedle5191",
                      "description": "Find beta replay fixtures."
                    },
                    {
                      "name": "{{ExcludedQueryName}}",
                      "query": "ExcludedNeedle5191",
                      "description": "Prove excluded selectors remain excluded."
                    }
                  ]
                }
              ]
            }
            """);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/space $folder/alpha one.cs",
            "csharp",
            "public class AlphaOne { string Value = \"AlphaNeedle5191\"; }\n");
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/space $folder/alpha & two.cs",
            "csharp",
            "public class AlphaTwo { string Value = \"AlphaNeedle5191\"; }\n");
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/space $folder/beta one.cs",
            "csharp",
            "public class BetaOne { string Value = \"BetaNeedle5191\"; }\n");
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/space $folder/beta ' two.cs",
            "csharp",
            "public class BetaTwo { string Value = \"BetaNeedle5191\"; }\n");
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/space $folder/ignored &/ignored.cs",
            "csharp",
            "public class Ignored { string Value = \"AlphaNeedle5191 BetaNeedle5191\"; }\n");
        return new FixtureScope(projectRoot, dbPath, Path.GetDirectoryName(dbPath)!, recipePath);
    }

    private sealed record ReplayQueryState(string Name, string? Cursor, string FirstPagePath);

    private sealed class FixtureScope(
        string ProjectRoot,
        string DbPath,
        string DataDir,
        string RecipePath) : IDisposable
    {
        internal string ProjectRoot { get; } = ProjectRoot;
        internal string DbPath { get; } = DbPath;
        internal string DataDir { get; } = DataDir;
        internal string RecipePath { get; } = RecipePath;

        public void Dispose() => TestProjectHelper.DeleteDirectory(ProjectRoot);
    }
}
