using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerIssue5210Tests
{
    [Fact]
    public void RunSearch_RecipeAndNamedQueryConsumeNoProgressAsGlobalFlag_Issue5210()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_search_global_flag_5210");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Issue5210.cs",
            "csharp",
            "public static class Issue5210 { public const string Needle = \"Issue5210Needle\"; }\n");

        var recipeArgs = new[]
        {
            new[]
            {
                "search", "--no-progress",
                "--recipe", "phrase-risk-patterns/obsolete-production-code",
                "--json=ndjson", "--results-only", "--limit", "1",
                "--db", dbPath, "--max-json-bytes", "1048576",
            },
            new[]
            {
                "search",
                "--recipe", "phrase-risk-patterns/obsolete-production-code",
                "--json=ndjson", "--results-only", "--limit", "1",
                "--db", dbPath, "--max-json-bytes", "1048576",
                "--no-progress",
            },
            new[]
            {
                "search",
                "--recipe", "phrase-risk-patterns/obsolete-production-code",
                "--json=ndjson", "--results-only", "--limit", "1",
                "--db", dbPath, "--max-json-bytes", "1048576",
                "--quiet",
            },
        };

        foreach (var args in recipeArgs)
        {
            var (exitCode, _, stderr) = CaptureConsole(() =>
                ProgramRunner.Run(args, JsonOptions, "1.44.3-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
        }

        var (namedExitCode, _, namedStderr) = CaptureConsole(() => ProgramRunner.Run(
            [
                "search", "--named-query", "needle=Issue5210Needle",
                "--db", dbPath, "--json=ndjson", "--results-only", "--limit", "1",
                "--no-progress",
            ],
            JsonOptions,
            "1.44.3-test"));

        Assert.Equal(CommandExitCodes.Success, namedExitCode);
        Assert.Equal(string.Empty, namedStderr);
    }

    [Fact]
    public void RunSearch_NoProgressRemainsAvailableAsOptionLikeAdHocQuery_Issue5210()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_search_literal_flag_5210");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/Issue5210.cs",
            "csharp",
            "// --no-progress remains searchable as literal query text.\n");

        var commands = new[]
        {
            new[] { "search", "--db", dbPath, "--json", "--count", "--no-progress" },
            new[] { "search", "--query", "--no-progress", "--db", dbPath, "--json", "--count" },
        };

        foreach (var args in commands)
        {
            var (exitCode, stdout, stderr) = CaptureConsole(() =>
                ProgramRunner.Run(args, JsonOptions, "1.44.3-test"));
            using var result = ParseJsonOutput(stdout);

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal("--no-progress", result.RootElement.GetProperty("query").GetString());
        }
    }
}
