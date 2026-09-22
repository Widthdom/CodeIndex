using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class QueryCommandRunnerSearchCancellationTests
{
    private static readonly string[][] OutputModes =
    [
        [], ["--json"], ["--count"], ["--count", "--json"], ["--format", "count"],
        ["--format", "grouped"], ["--group-by", "file", "--count"], ["--count-by", "path"],
    ];

    [Fact]
    public void PlainSearch_PreCancelledTokenPrecedesResults_Issue5421()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_search_cancel_5421");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/Widget.cs", "csharp", "public class Widget { }\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (var query in new[] { "NoMatch5421", "Widget" })
            foreach (var mode in OutputModes)
                foreach (var allowPartial in new[] { false, true })
                {
                    string[] args = [query, "--strict-not-found", "--db", dbPath, .. mode,
                        .. allowPartial ? new[] { "--allow-partial" } : Array.Empty<string>()];
                    var (_, stdout, stderr) = CaptureConsole(() =>
                    {
                        var exception = Assert.ThrowsAny<OperationCanceledException>(() =>
                            QueryCommandRunner.RunSearch(args, JsonOptions, cancellation.Token));
                        Assert.Equal(cancellation.Token, exception.CancellationToken);
                        return 0;
                    });
                    Assert.Empty(stdout);
                    Assert.Empty(stderr);
                }
    }

    [Fact]
    public void PlainSearch_CancellationInsideSqlitePropagatesAcrossEntryPoints_Issue5421()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_search_sql_cancel_5421");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/Widget.cs", "csharp", "public class Widget { }\n");
        var previousOpen = DbConnectionFactory.OpenReadOnlyForTesting;
        try
        {
            foreach (var entry in new[] { "direct", "cli", "batch-1", "batch-2" })
                foreach (var exactMiss in new[] { false, true })
                    foreach (var mode in new string[][] { [], ["--json"], ["--count"], ["--count", "--json"] })
                        foreach (var allowPartial in new[] { false, true })
                        {
                            using var cancellation = new CancellationTokenSource();
                            var callbacks = 0;
                            DbConnectionFactory.OpenReadOnlyForTesting = path =>
                            {
                                var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                                {
                                    DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
                                }.ToString());
                                connection.Open();
                                // Cancel inside the real search statement, after DB setup. Returning normally
                                // requires the production interrupt registration to stop the SQLite VM.
                                connection.CreateFunction<string, string, int>("instr", (text, query) =>
                                {
                                    if (exactMiss && query == "NoMatch5421")
                                    {
                                        Interlocked.Increment(ref callbacks);
                                        cancellation.Cancel();
                                    }
                                    return text.IndexOf(query, StringComparison.Ordinal) + 1;
                                });
                                connection.CreateFunction<string, string, string, int>("like", (pattern, pathValue, escape) =>
                                {
                                    if (!exactMiss && pattern == "src/%.cs")
                                    {
                                        Interlocked.Increment(ref callbacks);
                                        cancellation.Cancel();
                                    }
                                    return pathValue == "src/Widget.cs" ? 1 : 0;
                                });
                                return connection;
                            };
                            string[] args = [exactMiss ? "NoMatch5421" : "Widget", "--path", "src/*.cs",
                                "--strict-not-found", .. mode,
                                .. exactMiss ? new[] { "--exact" } : Array.Empty<string>(),
                                .. allowPartial ? new[] { "--allow-partial" } : Array.Empty<string>()];
                            (int Exit, string Stdout, string Stderr) result;
                            Exception? cancellationError = null;
                            if (entry.StartsWith("batch-", StringComparison.Ordinal))
                            {
                                result = CaptureConsoleWithInput(JsonSerializer.Serialize(new[] { "search" }.Concat(args)) + "\n",
                                    () => QueryCommandRunner.RunBatch(["--db", dbPath, "--json-summary", "--parallel", entry[6..]],
                                        JsonOptions, cancellationToken: cancellation.Token));
                            }
                            else
                            {
                                result = CaptureConsole(() =>
                                {
                                    if (entry == "cli")
                                        return ProgramRunner.Run(["search", .. args, "--db", dbPath], JsonOptions,
                                            configStartDirectory: project.Root, cancellationToken: cancellation.Token);
                                    cancellationError = Record.Exception(() =>
                                        QueryCommandRunner.RunSearch([.. args, "--db", dbPath], JsonOptions, cancellation.Token));
                                    return CommandExitCodes.CancelledBySignal;
                                });
                            }
                            Assert.True(callbacks > 0, $"Search statement was not reached: {entry}, {string.Join(' ', args)}");
                            Assert.Equal(CommandExitCodes.CancelledBySignal, result.Exit);
                            if (entry == "direct")
                            {
                                Assert.True(cancellationError is OperationCanceledException,
                                    $"Expected cancellation, got {cancellationError}; stdout={result.Stdout}; stderr={result.Stderr}");
                                var exception = (OperationCanceledException)cancellationError!;
                                Assert.Equal(cancellation.Token, exception.CancellationToken);
                                Assert.Equal(9, Assert.IsType<SqliteException>(exception.InnerException).SqliteErrorCode);
                                Assert.Empty(result.Stdout);
                                Assert.Empty(result.Stderr);
                            }
                            else if (entry.StartsWith("batch-", StringComparison.Ordinal))
                            {
                                Assert.Empty(result.Stderr);
                                var records = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .Select(line => JsonNode.Parse(line)!).ToArray();
                                Assert.Equal(2, records.Length);
                                Assert.Equal(CommandErrorCodes.Interrupted, records[0]["error"]!["error_code"]!.GetValue<string>());
                                Assert.Null(records[0]["result"]);
                                Assert.Null(records[0]["results"]);
                                Assert.Equal(CommandExitCodes.CancelledBySignal, records[1]["exit_code"]!.GetValue<int>());
                            }
                        }
        }
        finally
        {
            DbConnectionFactory.OpenReadOnlyForTesting = previousOpen;
        }
        var (retryExit, retryOutput, _) = CaptureConsole(() =>
            QueryCommandRunner.RunSearch(["Widget", "--count", "--db", dbPath], JsonOptions));
        Assert.Equal(CommandExitCodes.Success, retryExit);
        Assert.Equal("1", retryOutput.Trim());
    }
}
