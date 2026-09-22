using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class QueryCommandRunnerBatchIssue5344Tests
{
    [Fact]
    public void RunBatch_EmptyDiscoveryAndStrictCountsMatchDirect_Issue5393()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_empty_counts_5393");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/Widget.cs", "csharp", "public class Widget { }\n");
        var children = new List<(string[] Args, int Exit, JsonNode Expected, bool Ndjson)>();
        foreach (var command in new[] { "files", "symbols", "find", "search" })
            foreach (var query in new[] { "NoMatch5393", "Widget" })
                foreach (var strict in new[] { false, true })
                {
                    string[] args = [command, query,
                        .. command == "find" ? new[] { "--path", "src/Widget.cs" } : Array.Empty<string>(),
                        .. strict ? new[] { "--strict-not-found" } : Array.Empty<string>()];
                    var expectedExit = strict && query == "NoMatch5393" ? CommandExitCodes.NotFound : CommandExitCodes.Success;
                    var (humanExit, humanOutput, _) = RunDirect([.. args, "--count"], dbPath);
                    Assert.Equal(expectedExit, humanExit);
                    Assert.Equal(query == "NoMatch5393" ? "0" : "1", humanOutput.Trim());
                    foreach (var count in new string[][] { ["--count", "--json"], ["--format", "count"] })
                    {
                        string[] child = [.. args, .. count];
                        var direct = RunDirect(child, dbPath);
                        Assert.Equal(expectedExit, direct.Exit);
                        var payload = JsonNode.Parse(direct.Stdout)!;
                        Assert.Equal(query == "NoMatch5393" ? 0 : 1, payload["count"]!.GetValue<int>());
                        Assert.True(payload["authoritative_count"]!.GetValue<bool>());
                        Assert.False(payload["degraded"]!.GetValue<bool>());
                        children.Add((child, expectedExit, payload, false));
                    }
                    if (command is "files" or "symbols" && query == "NoMatch5393")
                    {
                        string[] child = [.. args, "--json"];
                        var direct = RunDirect(child, dbPath);
                        Assert.Equal(expectedExit, direct.Exit);
                        var records = ParseNdjson(direct.Stdout);
                        Assert.Single(records);
                        children.Add((child, expectedExit, records, true));
                    }
                    var budget = RunDirect([.. args, "--count", "--json", "--max-json-bytes", "1"], dbPath);
                    Assert.Equal(CommandExitCodes.UsageError, budget.Exit);
                }

        // The syntactically empty exact C# name has a separate count fast path.
        foreach (var flags in new string[][] { [], ["--json"], ["--json", "--group-partials"] })
        {
            var result = RunDirect(["symbols", "@", "--lang", "csharp", "--exact-name",
                "--count", "--strict-not-found", .. flags], dbPath);
            Assert.Equal(CommandExitCodes.NotFound, result.Exit);
        }

        foreach (var parallelism in new[] { "1", "3" })
        {
            var input = string.Join('\n', children.Select(child => JsonSerializer.Serialize(child.Args))) + "\n";
            var (exit, stdout, stderr) = CaptureConsoleWithInput(input, () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--parallel", parallelism, "--include-raw-streams"], JsonOptions));
            Assert.Equal(CommandExitCodes.NotFound, exit);
            Assert.Empty(stderr);
            var records = ParseNdjson(stdout);
            Assert.Equal(children.Count + 1, records.Count);
            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                var record = records[index]!;
                Assert.Equal(child.Exit, record["exit_code"]!.GetValue<int>());
                var actual = record[child.Ndjson ? "results" : "result"];
                if (child.Exit == CommandExitCodes.NotFound)
                {
                    Assert.Null(actual);
                    Assert.Equal("batch_child_not_found", record["error"]!["category"]!.GetValue<string>());
                    var raw = record["raw_streams"]!["stdout"]!.GetValue<string>();
                    actual = child.Ndjson ? ParseNdjson(raw) : JsonNode.Parse(raw);
                }
                Assert.True(JsonNode.DeepEquals(child.Expected, actual), $"{string.Join(' ', child.Args)}: {record}");
                Assert.Null(record["partial_result"]);
            }
            Assert.Equal(children.Count(child => child.Exit != CommandExitCodes.Success),
                records[^1]!["command_failures"]!.GetValue<int>());
        }

        // #5406: stale snapshots cannot become strict search misses.
        foreach (var allowPartial in new[] { false, true })
            foreach (var format in new string[][] { ["--count"], ["--count", "--json"], ["--format", "count"] })
            {
                string[] args = ["NoMatch5393", "--strict-not-found", .. format,
                    .. allowPartial ? new[] { "--allow-partial" } : Array.Empty<string>()];
                var snapshot = RunDirect(["search", .. args, "--read-only"], dbPath);
                Assert.Equal(CommandExitCodes.Success, snapshot.Exit);
                if (format.Length == 1)
                    Assert.Equal("0", snapshot.Stdout.Trim());
                else
                {
                    var payload = JsonNode.Parse(snapshot.Stdout)!;
                    Assert.Equal(0, payload["count"]!.GetValue<int>());
                    Assert.True(payload["wal_stale_snapshot_risk"]!.GetValue<bool>());
                    Assert.True(payload["degraded"]!.GetValue<bool>());
                    Assert.False(payload["authoritative_count"]!.GetValue<bool>());
                }
            }

        // Removing policy provenance makes symbol absence unknown, but cannot degrade file/text counts.
        using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
        using (var removePolicy = db.Connection.CreateCommand())
        {
            removePolicy.CommandText = $"DELETE FROM codeindex_meta WHERE key = '{DbContext.SymbolKindFilterMetaKey}'";
            removePolicy.ExecuteNonQuery();
        }
        foreach (var command in new[] { "files", "symbols", "find", "search" })
            foreach (var allowPartial in new[] { false, true })
            {
                string[] args = [command, "NoMatch5393", "--strict-not-found",
                    .. command == "find" ? new[] { "--path", "src/Widget.cs" } : Array.Empty<string>(),
                    .. allowPartial ? new[] { "--allow-partial" } : Array.Empty<string>()];
                foreach (var format in new string[][] { ["--count"], ["--count", "--json"] })
                {
                    var result = RunDirect([.. args, .. format], dbPath);
                    Assert.Equal(command == "symbols" ? CommandExitCodes.Success : CommandExitCodes.NotFound, result.Exit);
                    if (format.Length == 1)
                    {
                        Assert.Equal("0", result.Stdout.Trim());
                        if (command == "symbols")
                            Assert.Contains("coverage-limited", result.Stderr, StringComparison.Ordinal);
                    }
                    else
                    {
                        var payload = JsonNode.Parse(result.Stdout)!;
                        Assert.Equal(command != "symbols", payload["authoritative_count"]!.GetValue<bool>());
                    }
                }
                if (command == "symbols")
                {
                    foreach (var flags in new string[][] { [], ["--max-json-bytes", "4096"], ["--results-only"] })
                    {
                        var result = RunDirect([.. args, "--json", .. flags], dbPath);
                        Assert.Equal(CommandExitCodes.Success, result.Exit);
                        Assert.Contains("coverage-limited", result.Stderr, StringComparison.Ordinal);
                        if (flags.Contains("--results-only"))
                            Assert.Empty(result.Stdout);
                        else
                        {
                            var terminal = Assert.Single(ParseNdjson(result.Stdout))!;
                            Assert.False(terminal["total_count_authoritative"]!.GetValue<bool>());
                            Assert.False(terminal["authoritative_count"]!.GetValue<bool>());
                            Assert.False(terminal["index_complete"]!.GetValue<bool>());
                        }
                    }
                }
            }
    }

    [Fact]
    public void RunBatch_PartialFindPreservesRowsCursorAndFailureAccounting_Issue5344()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_partial_5344");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/matches.txt", "text", "alpha\nbeta alpha\nalpha\n");
        string[] child = ["find", "alpha", "--all", "--regex", "--json", "--line-scan-limit", "1"];
        var direct = RunDirect(child, dbPath);
        Assert.Equal(CommandExitCodes.PartialResult, direct.Exit);
        var expected = ParseNdjson(direct.Stdout);
        var terminal = expected[^1]!;
        Assert.True(terminal["partial_result"]!.GetValue<bool>());
        Assert.False(terminal["scan_complete"]!.GetValue<bool>());
        Assert.False(terminal["authoritative_rows"]!.GetValue<bool>());
        Assert.Equal("line_scan_limit", terminal["truncation_reason"]!.GetValue<string>());
        Assert.Equal("resume_with_next_cursor", terminal["continuation_action"]!.GetValue<string>());

        foreach (var parallelism in new[] { "1", "3" })
        {
            foreach (var raw in new[] { false, true })
            {
                var input = JsonSerializer.Serialize(child) + "\n[\"languages\",\"--format\",\"count\"]\n"
                    + "[\"search\",\"--limit\",\"invalid\",\"--json\"]\n";
                var (exit, stdout, stderr) = CaptureConsoleWithInput(input, () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", parallelism,
                        .. raw ? new[] { "--include-raw-streams" } : Array.Empty<string>()], JsonOptions));
                Assert.Equal(CommandExitCodes.PartialResult, exit);
                Assert.Empty(stderr);
                var records = ParseNdjson(stdout);
                Assert.Equal(4, records.Count);
                var first = records[0]!;
                Assert.Equal("error", first["status"]!.GetValue<string>());
                Assert.Equal(CommandExitCodes.PartialResult, first["exit_code"]!.GetValue<int>());
                Assert.True(first["partial_result"]!.GetValue<bool>());
                Assert.Equal(CommandErrorCodes.IndexPartial, first["error"]!["error_code"]!.GetValue<string>());
                Assert.True(JsonNode.DeepEquals(expected, first["results"]));
                Assert.Null(first["stdout"]);
                Assert.Null(first["stderr"]);
                Assert.Equal(raw, first["raw_streams"] is not null);
                if (raw)
                    Assert.Equal(direct.Stdout, first["raw_streams"]!["stdout"]!.GetValue<string>());
                Assert.Equal("ok", records[1]!["status"]!.GetValue<string>());
                Assert.Null(records[2]!["results"]);
                Assert.Null(records[2]!["result"]);
                Assert.Null(records[2]!["partial_result"]);
                for (var index = 0; index < 3; index++)
                    Assert.Equal(index + 1, records[index]!["line"]!.GetValue<int>());
                Assert.Equal(3, records[^1]!["commands_processed"]!.GetValue<int>());
                Assert.Equal(2, records[^1]!["command_failures"]!.GetValue<int>());
                Assert.Equal(stdout.Length, records[^1]!["output_chars"]!.GetValue<int>());

                var pages = new List<JsonNode> { first["results"]!.DeepClone() };
                var cursor = first["results"]!.AsArray()[^1]!["next_cursor"]!.GetValue<string>();
                for (var page = 0; cursor is not null && page < 4; page++)
                {
                    string[] resumed = [.. child, "--cursor", cursor];
                    var nextDirect = RunDirect(resumed, dbPath);
                    var (nextExit, nextStdout, _) = CaptureConsoleWithInput(JsonSerializer.Serialize(resumed) + "\n",
                        () => QueryCommandRunner.RunBatch(
                            ["--db", dbPath, "--json-summary", "--parallel", parallelism], JsonOptions));
                    Assert.Equal(nextDirect.Exit, nextExit);
                    var nextRecord = ParseNdjson(nextStdout)[0]!;
                    var next = nextRecord["result"] ?? nextRecord["results"]!;
                    var expectedNext = JsonNode.Parse(nextDirect.Stdout)!;
                    RemoveTiming(expectedNext);
                    RemoveTiming(next);
                    Assert.True(JsonNode.DeepEquals(expectedNext, next));
                    var nextTerminal = next["metadata"]!["stream_terminal"]!;
                    if (nextExit == CommandExitCodes.PartialResult)
                    {
                        Assert.False(nextTerminal["authoritative_rows"]!.GetValue<bool>());
                        Assert.True(nextRecord["partial_result"]!.GetValue<bool>());
                    }
                    pages.Add(next["results"]!.DeepClone());
                    cursor = nextTerminal["next_cursor"]?.GetValue<string>();
                }
                Assert.Null(cursor);
                var matches = pages.SelectMany(page => page.AsArray())
                    .Where(row => row?["terminal_record"] is null)
                    .Select(row => row!["line"]!.GetValue<int>()).ToArray();
                Assert.Equal(new[] { 1, 2, 3 }, matches);
            }

            foreach (var query in new[] { "alpha", "beta" })
                foreach (var diagnosticFlags in new string[][] { ["--verbose"], ["--profile"], ["--verbose", "--profile"] })
                {
                    string[] diagnosticChild = ["find", query, .. child.Skip(2), .. diagnosticFlags];
                    var diagnosticDirect = RunDirect(diagnosticChild, dbPath);
                    Assert.Equal(CommandExitCodes.PartialResult, diagnosticDirect.Exit);
                    var expectedDiagnostics = ParseNdjson(diagnosticDirect.Stdout);
                    foreach (var flag in diagnosticFlags)
                        Assert.Single(expectedDiagnostics.Where(row => row?[flag == "--verbose" ? "_debug" : "profile"] is JsonObject));
                    var (exit, stdout, _) = CaptureConsoleWithInput(JsonSerializer.Serialize(diagnosticChild) + "\n",
                        () => QueryCommandRunner.RunBatch(
                            ["--db", dbPath, "--json-summary", "--parallel", parallelism], JsonOptions));
                    Assert.Equal(CommandExitCodes.PartialResult, exit);
                    var records = ParseNdjson(stdout);
                    var record = records[0]!;
                    Assert.Equal("error", record["status"]!.GetValue<string>());
                    Assert.True(record["partial_result"]!.GetValue<bool>());
                    var actualDiagnostics = Assert.IsType<JsonArray>(record["results"]);
                    // Batch context reuse changes SQL counts and timings, but not result rows or terminals.
                    foreach (var flag in diagnosticFlags)
                    {
                        var key = flag == "--verbose" ? "_debug" : "profile";
                        var control = Assert.Single(actualDiagnostics.Where(row => row?[key] is JsonObject))!;
                        Assert.Single(control.AsObject());
                        var diagnostic = control[key]!;
                        var phases = Assert.IsType<JsonArray>(diagnostic["phases"]);
                        if (flag == "--verbose")
                            Assert.Equal(phases.Count, diagnostic["sql_statement_count"]!.GetValue<int>());
                        else
                        {
                            Assert.IsType<JsonArray>(diagnostic["query_plan"]);
                            Assert.IsType<JsonArray>(diagnostic["queries"]);
                        }
                    }
                    static JsonArray WithoutDiagnostics(JsonArray items) => new(items
                        .Where(item => item?["_debug"] is null && item?["profile"] is null)
                        .Select(item => item!.DeepClone()).ToArray());
                    Assert.True(JsonNode.DeepEquals(WithoutDiagnostics(expectedDiagnostics), WithoutDiagnostics(actualDiagnostics)),
                        $"{string.Join(' ', diagnosticChild)}: {actualDiagnostics}");
                    Assert.Null(record["raw_streams"]);
                    Assert.Null(record["stdout"]);
                    Assert.Equal(1, records[^1]!["command_failures"]!.GetValue<int>());
                }

            var (limitedExit, limitedOutput, _) = CaptureConsoleWithInput(JsonSerializer.Serialize(child) + "\n",
                () => QueryCommandRunner.RunBatch(
                    ["--db", dbPath, "--json-summary", "--parallel", parallelism,
                        "--max-output-chars", QueryCommandRunner.BatchMinTotalOutputChars.ToString()], JsonOptions));
            Assert.Equal(CommandExitCodes.InvalidArgument, limitedExit);
            Assert.True(limitedOutput.Length <= QueryCommandRunner.BatchMinTotalOutputChars);
            var limited = ParseNdjson(limitedOutput);
            Assert.Equal("batch_output_limit", limited[0]!["error"]!["category"]!.GetValue<string>());
            Assert.Equal(CommandExitCodes.PartialResult, limited[0]!["error"]!["attempted_exit_code"]!.GetValue<int>());
            Assert.Null(limited[0]!["results"]);
            Assert.True(limited[^1]!["output_limit_reached"]!.GetValue<bool>());
            Assert.Equal(limitedOutput.Length, limited[^1]!["output_chars"]!.GetValue<int>());
        }
    }

    [Fact]
    public void RunBatch_PartialFindCountEnvelopeAndZeroRowsMatchDirect_Issue5344()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_partial_shapes_5344");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/matches.txt", "text", "alpha\nbeta\n");
        foreach (var scope in new string[][]
        {
            ["--all", "--line-scan-limit", "1"],
            ["--path", "src/matches.txt", "--exclude-comments"],
            ["--path", "src/matches.txt", "--origin", "unknown"],
            ["--path", "src/matches.txt", "--exclude-origin=unknown"],
        })
            foreach (var query in new[] { "alpha", "beta" })
                foreach (var flags in new string[][]
                {
            ["--json"], ["--json", "--count"], ["--json-envelope"], ["--json-envelope", "--count"],
            ["--json", "--fields", "path", "--max-json-bytes", "6000"],
                })
                {
                    string[] child = ["find", query, "--regex", .. scope, .. flags];
                    var direct = RunDirect(child, dbPath);
                    Assert.Equal(CommandExitCodes.PartialResult, direct.Exit);
                    var ndjson = flags.SequenceEqual(new[] { "--json" });
                    var expected = ndjson ? ParseNdjson(direct.Stdout) : JsonNode.Parse(direct.Stdout)!;
                    RemoveTiming(expected);
                    foreach (var parallelism in new[] { "1", "3" })
                    {
                        var (exit, stdout, _) = CaptureConsoleWithInput(JsonSerializer.Serialize(child) + "\n",
                            () => QueryCommandRunner.RunBatch(
                                ["--db", dbPath, "--json-summary", "--parallel", parallelism], JsonOptions));
                        Assert.Equal(direct.Exit, exit);
                        var record = ParseNdjson(stdout)[0]!;
                        Assert.True(record["partial_result"]!.GetValue<bool>());
                        var actual = record[ndjson ? "results" : "result"]!;
                        RemoveTiming(actual);
                        Assert.True(JsonNode.DeepEquals(expected, actual));
                    }
                }
    }

    [Fact]
    public void RunBatch_DefinitionCardinalityAndOutputVariantsMatchDirect_Issue5344()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_definition_5344");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(dbPath, "src/first.cs", "csharp", "class Widget { }\nclass Solo { }\n");
        TestProjectHelper.InsertIndexedFile(dbPath, "src/second.cs", "csharp", "namespace Other { class Widget { } }\n");
        var children = new List<(string[] Args, int Exit, JsonNode? Expected, bool Ndjson)>();
        foreach (var query in new[] { "Widget", "Solo", "NoDefinition5344" })
            foreach (var flags in new string[][]
            {
            ["--json"], ["--json=ndjson", "--body"], ["--format", "json"],
            ["--json=array", "--body"], ["--compact", "--body"], ["--format", "compact"],
            ["--json-envelope", "--body"], ["--json", "--fields", "name,path"],
            ["--json", "--count"], ["--format", "lsp"], ["--format", "sarif"],
            })
            {
                string[] child = ["definition", query, "--limit", "10", .. flags];
                var direct = RunDirect(child, dbPath);
                var ndjson = flags[0] is "--json=ndjson" || flags.SequenceEqual(new[] { "--json" })
                    || flags.SequenceEqual(new[] { "--format", "json" });
                var expected = string.IsNullOrWhiteSpace(direct.Stdout) ? null
                    : ndjson ? ParseNdjson(direct.Stdout) : JsonNode.Parse(direct.Stdout)!;
                if (direct.Exit == 0 && ndjson)
                    Assert.Equal(query == "Widget" ? 2 : 1, expected!.AsArray().Count);
                if (expected is not null)
                    RemoveTiming(expected);
                children.Add((child, direct.Exit, expected, ndjson));
            }
        var input = string.Join('\n', children.Select(child => JsonSerializer.Serialize(child.Args))) + "\n";
        foreach (var parallelism in new[] { "1", "3" })
        {
            var (_, stdout, stderr) = CaptureConsoleWithInput(input, () => QueryCommandRunner.RunBatch(
                ["--db", dbPath, "--json-summary", "--parallel", parallelism], JsonOptions));
            Assert.Empty(stderr);
            var records = ParseNdjson(stdout);
            Assert.Equal(children.Count + 1, records.Count);
            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                var record = records[index]!;
                Assert.Equal(index + 1, record["line"]!.GetValue<int>());
                Assert.Equal(child.Exit, record["exit_code"]!.GetValue<int>());
                Assert.Null(record["stdout"]);
                if (child.Exit != 0)
                {
                    Assert.Contains(child.Exit, new[] { CommandExitCodes.NotFound, CommandExitCodes.UsageError });
                    Assert.NotNull(record["error"]);
                    Assert.Null(record["results"]);
                    continue;
                }
                var actual = record[child.Ndjson ? "results" : "result"]!;
                RemoveTiming(actual);
                Assert.True(JsonNode.DeepEquals(child.Expected, actual), $"{string.Join(' ', child.Args)}: {actual}");
            }
        }
    }

    private static (int Exit, string Stdout, string Stderr) RunDirect(string[] child, string dbPath)
    {
        string[] args = [.. child.Skip(1), "--db=" + dbPath];
        int Run(string[] effective) => child[0] switch
        {
            "find" => QueryCommandRunner.RunFind(effective, JsonOptions),
            "files" => QueryCommandRunner.RunFiles(effective, JsonOptions),
            "symbols" => QueryCommandRunner.RunSymbols(effective, JsonOptions),
            "search" => QueryCommandRunner.RunSearch(effective, JsonOptions),
            _ => QueryCommandRunner.RunDefinition(effective, JsonOptions),
        };
        return CaptureConsole(() => JsonEnvelopeWrapper.ShouldWrap(child[0], args)
            ? JsonEnvelopeWrapper.RunWrapped(child[0], args, "", JsonOptions, Run)
            : Run(args));
    }

    private static JsonArray ParseNdjson(string stdout)
        => new(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)).ToArray());

    private static void RemoveTiming(JsonNode node)
    {
        if (node is JsonObject obj && obj["metadata"] is JsonObject metadata)
            metadata.Remove("elapsed_ms");
    }
}
