using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class QueryCommandRunnerIssue4830Tests
{
    [Fact]
    public void RunSymbols_CSharpStaticLambdaCorpusHasNoPhantomDeclarations_Issue4830()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_symbols_static_lambda_4830");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/StaticLambdaCorpus.cs",
            "csharp",
            """
            using System;

            public static class StaticLambdaCorpus
            {
                public static string Fold(string value)
                {
                    return string.Create(
                        value.Length,
                        (Value: value, Offset: 0),
                        static (
                            Span<char> destination,
                            (string Value, int Offset) state) =>
                        {
                            state.Value.AsSpan().CopyTo(destination);
                        });
                }
            }
            """);

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSymbols(
            ["--db", dbPath, "--json", "--lang", "csharp"],
            JsonOptions));
        var rows = ParseJsonLines(stdout);
        var symbols = rows
            .Select(row => (
                Kind: row.RootElement.GetProperty("kind").GetString(),
                Name: row.RootElement.GetProperty("name").GetString()))
            .ToList();

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.DoesNotContain(symbols, symbol => symbol.Kind == "function" && symbol.Name == "static");
        Assert.DoesNotContain(
            symbols,
            symbol => (symbol.Kind is "function" or "property")
                && (symbol.Name is "destination" or "state"));
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Fold");
    }
}
