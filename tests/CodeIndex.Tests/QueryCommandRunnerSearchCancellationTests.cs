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

    private static readonly string[][] NamedRecipeRoutes =
    [
        ["--named-query", "probe=Widget"],
        ["--named-query", "probe=Widget", "--count"],
        ["--named-query", "probe=Widget", "--format", "compact"],
        ["--recipe", "risky-code/unbounded-json-parse"],
        ["--recipe", "risky-code/unbounded-json-parse", "--count"],
        ["--recipe", "risky-code/unbounded-json-parse", "--format", "count", "--summary-only"],
        ["--recipe", "risky-code/unbounded-json-parse", "--format", "compact"],
        ["--recipe", "risky-code/unbounded-json-parse", "--json=ndjson"],
        ["--recipe", "risky-code/unbounded-json-parse", "--count-by", "path"],
        ["--recipe", "risky-code/unbounded-json-parse", "--unique", "path"],
        ["--recipe", "risky-code/unbounded-json-parse", "--group-by", "file", "--count"],
        ["--recipe", "risky-code/unbounded-json-parse", "--format", "issue-drafts"],
        ["Widget", "--format", "issue-drafts"],
    ];

    [Fact]
    public void NamedRecipeSearch_PreCancelledAndManagedCancellationEmitNoSuccess_Issue5427()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_named_recipe_cancel_5427");
        var dbPath = CreateNamedRecipeCancellationDb(project.Root);
        var previousHook = QueryCommandRunner.SearchDisplayRowPreparedForTesting;
        try
        {
            foreach (var route in NamedRecipeRoutes)
                foreach (var json in new[] { false, true })
                    foreach (var allowPartial in new[] { false, true })
                    {
                        string[] args = [.. route, "--db", dbPath, "--strict-not-found",
                            .. json && !route.Contains("issue-drafts") ? new[] { "--json" } : Array.Empty<string>(),
                            .. allowPartial ? new[] { "--allow-partial" } : Array.Empty<string>()];
                        QueryCommandRunner.SearchDisplayRowPreparedForTesting = null;
                        var control = CaptureConsole(() => QueryCommandRunner.RunSearch(args, JsonOptions));
                        Assert.True(control.Result == CommandExitCodes.Success,
                            $"Control failed: {string.Join(' ', args)}; {control.Stdout}; {control.Stderr}");
                        Assert.NotEmpty(control.Stdout);
                        foreach (var cancelAt in new[] { 0, 1, 2 })
                        {
                            using var cancellation = new CancellationTokenSource();
                            var rowsPrepared = 0;
                            QueryCommandRunner.SearchDisplayRowPreparedForTesting = () =>
                            {
                                if (++rowsPrepared == cancelAt)
                                    cancellation.Cancel();
                            };
                            if (cancelAt == 0)
                                cancellation.Cancel();
                            var (_, stdout, stderr) = CaptureConsole(() =>
                            {
                                var error = Assert.ThrowsAny<OperationCanceledException>(() =>
                                    QueryCommandRunner.RunSearch(args, JsonOptions, cancellation.Token));
                                Assert.Equal(cancellation.Token, error.CancellationToken);
                                return 0;
                            });
                            Assert.Equal(cancelAt, rowsPrepared);
                            Assert.Empty(stdout);
                            Assert.Empty(stderr);
                        }
                    }
        }
        finally
        {
            QueryCommandRunner.SearchDisplayRowPreparedForTesting = previousHook;
        }
    }

    [Fact]
    public void NamedRecipeSearch_SqliteCancellationRetainsEntryPointClassification_Issue5427()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_named_recipe_sql_cancel_5427");
        var dbPath = CreateNamedRecipeCancellationDb(project.Root);
        var previousOpen = DbConnectionFactory.OpenReadOnlyForTesting;
        try
        {
            foreach (var entry in new[] { "direct", "cli", "batch-1", "batch-2" })
                foreach (var route in NamedRecipeRoutes)
                {
                    using var cancellation = new CancellationTokenSource();
                    var callbacks = 0;
                    DbConnectionFactory.OpenReadOnlyForTesting = path =>
                    {
                        var connection = OpenCancellationTestConnection(path);
                        connection.CreateFunction<string, string, string, int>("like", (pattern, pathValue, escape) =>
                        {
                            if (pattern == "src/%.cs")
                            {
                                Interlocked.Increment(ref callbacks);
                                cancellation.Cancel();
                            }
                            return 1;
                        });
                        return connection;
                    };
                    string[] args = [.. route, "--path", "src/*.cs", "--strict-not-found", "--allow-partial",
                        .. route.Contains("issue-drafts") ? Array.Empty<string>() : new[] { "--json" }];
                    (int Exit, string Stdout, string Stderr) result;
                    Exception? error = null;
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
                            error = Record.Exception(() => QueryCommandRunner.RunSearch(
                                [.. args, "--db", dbPath], JsonOptions, cancellation.Token));
                            return CommandExitCodes.CancelledBySignal;
                        });
                    }
                    Assert.True(callbacks > 0,
                        $"SQL not reached: {entry}, {string.Join(' ', route)}; exit={result.Exit}; stdout={result.Stdout}; stderr={result.Stderr}");
                    Assert.Equal(CommandExitCodes.CancelledBySignal, result.Exit);
                    if (entry == "direct")
                    {
                        var cancellationError = Assert.IsAssignableFrom<OperationCanceledException>(error);
                        Assert.Equal(cancellation.Token, cancellationError.CancellationToken);
                        Assert.Equal(9, Assert.IsType<SqliteException>(cancellationError.InnerException).SqliteErrorCode);
                        Assert.Empty(result.Stdout);
                        Assert.Empty(result.Stderr);
                    }
                    else if (entry == "cli")
                    {
                        Assert.Empty(result.Stdout);
                        Assert.Contains("Error: command cancelled before it could complete.", result.Stderr);
                    }
                    else
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
    }

    [Fact]
    public void NamedRecipeSearch_NestedCancellationRestoresReusedBatchReader_Issue5427()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_named_recipe_reuse_5427");
        var dbPath = CreateNamedRecipeCancellationDb(project.Root);
        var previousOpen = DbConnectionFactory.OpenReadOnlyForTesting;
        var previousHook = QueryCommandRunner.SearchDisplayRowPreparedForTesting;
        try
        {
            var opens = 0;
            DbConnectionFactory.OpenReadOnlyForTesting = path =>
            {
                opens++;
                return OpenCancellationTestConnection(path);
            };
            var nestedCalls = 0;
            QueryCommandRunner.SearchDisplayRowPreparedForTesting = () =>
            {
                // Execute narrower child lifetimes inside the inherited batch reader, then
                // retry with the outer lifetime. No extra connection may hide a leaked token.
                foreach (var route in NamedRecipeRoutes)
                {
                    using var child = new CancellationTokenSource();
                    QueryCommandRunner.SearchDisplayRowPreparedForTesting = child.Cancel;
                    var error = Assert.ThrowsAny<OperationCanceledException>(() =>
                        QueryCommandRunner.RunSearch(route, JsonOptions, child.Token));
                    Assert.Equal(child.Token, error.CancellationToken);
                    QueryCommandRunner.SearchDisplayRowPreparedForTesting = null;
                    Assert.Equal(CommandExitCodes.Success,
                        QueryCommandRunner.RunSearch(route, JsonOptions));
                    nestedCalls++;
                }
            };
            var input = JsonSerializer.Serialize(new[] { "search", "--named-query", "outer=Widget", "--count" }) + "\n";
            var result = CaptureConsoleWithInput(input,
                () => QueryCommandRunner.RunBatch(["--db", dbPath], JsonOptions));
            Assert.Equal(CommandExitCodes.Success, result.Result);
            Assert.Equal(NamedRecipeRoutes.Length, nestedCalls);
            Assert.Equal(1, opens);
        }
        finally
        {
            QueryCommandRunner.SearchDisplayRowPreparedForTesting = previousHook;
            DbConnectionFactory.OpenReadOnlyForTesting = previousOpen;
        }
    }

    private static string CreateNamedRecipeCancellationDb(string root)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/First.cs", "csharp",
            "public class Widget { void M() { JsonDocument.Parse(input); } }\n");
        TestProjectHelper.InsertIndexedFile(dbPath, "src/Second.cs", "csharp",
            "namespace Other { public class Widget { void M() { JsonDocument.Parse(input); } } }\n");
        return dbPath;
    }

    private static SqliteConnection OpenCancellationTestConnection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    [Fact]
    public void PlainSearch_ManagedOriginsRetainScopedCancellationAndReaderRestoresLifetime_Issue5421()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_search_origin_cancel_5421");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "Widget.cs", "csharp", "class Widget { }\n");
        TestProjectHelper.InsertIndexedFile(dbPath, "Widget.py", "python", "class Widget:\n    pass\n");
        using var db = new DbContext(DbOpenIntent.QueryOnly, dbPath);
        using var lifetime = new CancellationTokenSource();
        using var reader = new DbReader(db, lifetime.Token);
        foreach (var language in new[] { "csharp", "python" })
        {
            using var cancellation = new CancellationTokenSource();
            using (reader.BeginCancellationScope(cancellation.Token))
            {
                var row = Assert.Single(reader.Search("Widget", lang: language));
                Action classify = () =>
                {
                    if (language == "csharp")
                        _ = row.CSharpOrigins!.GetOrigin(1, "class Widget { }", 6);
                    else
                        _ = row.PythonOrigins!.GetOrigin(1, "class Widget:", 6);
                };
                classify();
                cancellation.Cancel();
                var error = Assert.ThrowsAny<OperationCanceledException>(classify);
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            Assert.Equal(lifetime.Token, reader.Cancellation);
            Assert.Single(reader.Search("Widget", lang: language));
        }
        lifetime.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(reader.ThrowIfCancellationRequested);
    }

    [Fact]
    public void Search_ManagedAggregationCancellationEmitsNoSuccess_Issue5421_Issue5427()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_search_group_cancel_5421");
        var dbPath = CreateNamedRecipeCancellationDb(project.Root);
        var previousHook = QueryCommandRunner.SearchAggregationGroupPreparedForTesting;
        try
        {
            foreach (var route in new string[][] { ["Widget"], ["--recipe", "risky-code/unbounded-json-parse"] })
                foreach (var mode in new string[][]
            {
                ["--group-by", "file", "--count"], ["--group-by", "symbol", "--count"],
                ["--count-by", "path"], ["--unique", "path"],
            })
                foreach (var json in new[] { false, true })
                    foreach (var allowPartial in new[] { false, true })
                        foreach (var cancelAt in new[] { 1, 2 })
                        {
                            using var cancellation = new CancellationTokenSource();
                            var groupsPrepared = 0;
                            QueryCommandRunner.SearchAggregationGroupPreparedForTesting = () =>
                            {
                                if (++groupsPrepared == cancelAt)
                                    cancellation.Cancel();
                            };
                            var (_, stdout, stderr) = CaptureConsole(() =>
                            {
                                var error = Assert.ThrowsAny<OperationCanceledException>(() => QueryCommandRunner.RunSearch(
                                    [.. route, "--db", dbPath, "--strict-not-found", .. mode,
                                        .. json ? new[] { "--json" } : Array.Empty<string>(),
                                        .. allowPartial ? new[] { "--allow-partial" } : Array.Empty<string>()],
                                    JsonOptions, cancellation.Token));
                                Assert.Equal(cancellation.Token, error.CancellationToken);
                                return 0;
                            });
                            Assert.Equal(cancelAt, groupsPrepared);
                            Assert.Empty(stdout);
                            Assert.Empty(stderr);
                        }
        }
        finally
        {
            QueryCommandRunner.SearchAggregationGroupPreparedForTesting = previousHook;
        }
    }

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
                                    DataSource = path,
                                    Mode = SqliteOpenMode.ReadOnly,
                                    Pooling = false,
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
                            else if (entry == "cli")
                            {
                                Assert.Empty(result.Stdout);
                                Assert.Contains("Error: command cancelled before it could complete.", result.Stderr);
                                Assert.DoesNotContain(CommandErrorCodes.Interrupted, result.Stderr);
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
