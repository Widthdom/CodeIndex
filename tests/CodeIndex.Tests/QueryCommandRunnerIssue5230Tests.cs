using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerIssue5230Tests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void Symbols_NdjsonCursorWalksEveryPageAndFinalPageOmitsContinuation_Issue5230()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ndjson_symbols_cursor_5230");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 1; index <= 5; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/Issue5230Type{index}.cs",
                    "csharp",
                    $"public sealed class Issue5230Type{index} {{ }}\n");
            }

            var args = new[]
            {
                "symbols", "Issue5230Type", "--db", dbPath, "--json", "--limit", "2",
            };
            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() =>
                ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            var firstRecords = ParseNdjson(firstStdout);
            Assert.Equal(3, firstRecords.Length);
            var names = firstRecords[..^1]
                .Select(row => row.GetProperty("name").GetString())
                .ToList();
            var terminal = firstRecords[^1];
            Assert.True(terminal.GetProperty("has_more").GetBoolean());
            var cursor = Assert.IsType<string>(terminal.GetProperty("next_cursor").GetString());
            Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);

            JsonElement finalMetadata = default;
            while (cursor is not null)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                    args.Concat(["--cursor", cursor]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                names.AddRange(document.RootElement.GetProperty("results")
                    .EnumerateArray()
                    .Select(row => row.GetProperty("name").GetString()));
                finalMetadata = document.RootElement.GetProperty("metadata").Clone();
                cursor = finalMetadata.GetProperty("next_cursor").GetString();
            }

            Assert.Equal(5, names.Count);
            Assert.Equal(5, names.Distinct(StringComparer.Ordinal).Count());
            Assert.False(finalMetadata.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, finalMetadata.GetProperty("next_cursor").ValueKind);
            var finalTerminal = finalMetadata.GetProperty("stream_terminal");
            Assert.False(finalTerminal.GetProperty("has_more").GetBoolean());
            Assert.False(finalTerminal.TryGetProperty("next_cursor", out _));
            Assert.False(finalTerminal.TryGetProperty("next_cursor_unavailable_reason", out _));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SearchAndFiles_NdjsonTerminalsShareTheResumableCursorContract_Issue5230()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ndjson_shared_cursor_5230");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 1; index <= 3; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/Issue5230Needle{index}.txt",
                    "text",
                    $"Issue5230Needle marker {index}\n");
            }

            AssertRawCursorResumesWithoutRepeatingFirstPath(
                ["search", "Issue5230Needle", "--db", dbPath, "--exact-substring", "--json", "--limit", "1"]);
            AssertRawCursorResumesWithoutRepeatingFirstPath(
                ["files", "--path", "src/*.txt", "--db", dbPath, "--json", "--limit", "1"]);

            var (selectorExitCode, selectorStdout, selectorStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    [
                        "search", "Issue5230Needle", "--db", dbPath, "--exact-substring",
                        "--json", "--limit", "1", "--first-per-file",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, selectorExitCode);
            Assert.Equal(string.Empty, selectorStderr);
            var selectorTerminal = ParseNdjson(selectorStdout)[^1];
            Assert.True(selectorTerminal.GetProperty("has_more").GetBoolean());
            Assert.False(selectorTerminal.TryGetProperty("next_cursor", out _));
            Assert.Equal(
                "stream_not_cursor_capable",
                selectorTerminal.GetProperty("next_cursor_unavailable_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Symbols_NdjsonSuppressesCursorWhenGenerationChangesDuringQuery_Issue5230()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ndjson_cursor_generation_race_5230");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 1; index <= 3; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/Issue5230Race{index}.cs",
                    "csharp",
                    $"public sealed class Issue5230Race{index} {{ }}\n");
            }

            var generationChanged = false;
            QueryCommandRunner.NdjsonRowsMaterializedForTesting = () =>
            {
                if (generationChanged)
                    return;

                generationChanged = true;
                using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(
                    DbContext.IndexedHeadTimestampMetaKey,
                    "2026-09-01T23:59:59.0000000+00:00");
            };

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["symbols", "Issue5230Race", "--db", dbPath, "--json", "--limit", "1"],
                _jsonOptions,
                "1.0.0-test"));

            Assert.True(generationChanged);
            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            var terminal = ParseNdjson(stdout)[^1];
            Assert.True(terminal.GetProperty("has_more").GetBoolean());
            Assert.False(terminal.TryGetProperty("next_cursor", out _));
            Assert.Equal(
                "index_generation_changed_during_query",
                terminal.GetProperty("next_cursor_unavailable_reason").GetString());
        }
        finally
        {
            QueryCommandRunner.NdjsonRowsMaterializedForTesting = null;
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Symbols_NdjsonSuppressesUnusableCursorAtPaginationWindow_Issue5230()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ndjson_cursor_window_5230");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var source = string.Join(
                '\n',
                Enumerable.Range(0, 10_001)
                    .Select(index => $"public sealed class Issue5230Window{index:D5} {{ }}"));
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Issue5230Window.cs",
                "csharp",
                source);

            var args = new[]
            {
                "symbols", "Issue5230Window", "--db", dbPath, "--json", "--limit", "1",
            };
            var (_, firstStdout, _) = CaptureConsole(() =>
                ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));
            var firstCursor = Assert.IsType<string>(ParseNdjson(firstStdout)[^1]
                .GetProperty("next_cursor")
                .GetString());
            var boundaryCursor = ReplaceResponseCursorOffset(firstCursor, 9_999);

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                args.Concat(["--cursor", boundaryCursor]).ToArray(),
                _jsonOptions,
                "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var metadata = document.RootElement.GetProperty("metadata");
            Assert.True(metadata.GetProperty("pagination_window_exhausted").GetBoolean());
            Assert.False(metadata.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("next_cursor").ValueKind);
            var terminal = metadata.GetProperty("stream_terminal");
            Assert.True(terminal.GetProperty("has_more").GetBoolean());
            Assert.False(terminal.TryGetProperty("next_cursor", out _));
            Assert.Equal(
                "pagination_window_exhausted",
                terminal.GetProperty("next_cursor_unavailable_reason").GetString());

            var (largePageExitCode, largePageStdout, largePageStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    [
                        "symbols", "Issue5230Window", "--db", dbPath,
                        "--json", "--limit", "6000",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, largePageExitCode);
            Assert.Equal(string.Empty, largePageStderr);
            var largePageTerminal = ParseNdjson(largePageStdout)[^1];
            Assert.True(largePageTerminal.GetProperty("has_more").GetBoolean());
            Assert.False(largePageTerminal.TryGetProperty("next_cursor", out _));
            Assert.Equal(
                "pagination_window_exhausted",
                largePageTerminal.GetProperty("next_cursor_unavailable_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Symbols_NdjsonCursorRejectsFilterMismatchAndIndexGenerationChange_Issue5230()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ndjson_cursor_validation_5230");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 1; index <= 3; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/Issue5230Validation{index}.cs",
                    "csharp",
                    $"public sealed class Issue5230Validation{index} {{ }}\n");
            }

            var args = new[]
            {
                "symbols", "Issue5230Validation", "--kind", "class",
                "--db", dbPath, "--json", "--limit", "1",
            };
            var (_, firstStdout, _) = CaptureConsole(() =>
                ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));
            var cursor = Assert.IsType<string>(ParseNdjson(firstStdout)[^1]
                .GetProperty("next_cursor")
                .GetString());

            var (mismatchExitCode, mismatchStdout, mismatchStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    [
                        "symbols", "Issue5230Validation", "--kind", "interface",
                        "--db", dbPath, "--json", "--limit", "1", "--cursor", cursor,
                    ],
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.UsageError, mismatchExitCode);
            Assert.Equal(string.Empty, mismatchStdout);
            Assert.Contains("does not match this command, query, or filter set", mismatchStderr, StringComparison.Ordinal);

            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
            {
                var writer = new DbWriter(db.Connection);
                writer.SetMeta(
                    DbContext.IndexedHeadTimestampMetaKey,
                    "2026-08-31T23:59:59.0000000+00:00");
            }

            var (staleExitCode, staleStdout, staleStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    args.Concat(["--cursor", cursor]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.UsageError, staleExitCode);
            Assert.Equal(string.Empty, staleStdout);
            Assert.Contains("index generation changed", staleStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Search_ZeroAndByteBudgetedTerminalsKeepContinuationProgressTruthful_Issue5230()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("ndjson_cursor_budget_5230");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 1; index <= 10; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/Issue5230Large{index}.txt",
                    "text",
                    $"Issue5230Large {index} {new string('x', 4_000)}\n");
            }

            var (zeroExitCode, zeroStdout, zeroStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    ["search", "Issue5230Missing", "--db", dbPath, "--json", "--limit", "1"],
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, zeroExitCode);
            Assert.Equal(string.Empty, zeroStderr);
            var zeroTerminal = ParseNdjson(zeroStdout)[^1];
            Assert.False(zeroTerminal.GetProperty("has_more").GetBoolean());
            Assert.False(zeroTerminal.TryGetProperty("next_cursor", out _));

            var (_, firstPageStdout, _) = CaptureConsole(() => ProgramRunner.Run(
                [
                    "search", "Issue5230Large", "--db", dbPath, "--exact-substring",
                    "--json", "--limit", "1", "--max-line-width", "4096",
                ],
                _jsonOptions,
                "1.0.0-test"));
            var firstPageRecords = ParseNdjson(firstPageStdout);
            var firstPagePath = firstPageRecords[0].GetProperty("path").GetString();
            var firstPageCursor = Assert.IsType<string>(firstPageRecords[^1]
                .GetProperty("next_cursor")
                .GetString());
            JsonElement trimmedMetadata = default;
            JsonElement[] trimmedResults = [];
            var trimmedBudget = 0;
            foreach (var budget in Enumerable.Range(6, 25).Select(index => index * 1_000))
            {
                var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                    [
                        "search", "Issue5230Large", "--db", dbPath, "--exact-substring",
                        "--json", "--limit", "8", "--max-line-width", "4096",
                        "--cursor", firstPageCursor, "--max-json-bytes", budget.ToString(),
                    ],
                    _jsonOptions,
                    "1.0.0-test"));
                if (exitCode != CommandExitCodes.Success || string.IsNullOrWhiteSpace(stdout))
                    continue;

                using var document = JsonDocument.Parse(stdout);
                var results = document.RootElement.GetProperty("results");
                if (results.GetArrayLength() is <= 0 or >= 8)
                    continue;

                Assert.True(Encoding.UTF8.GetByteCount(stdout) <= budget);
                trimmedMetadata = document.RootElement.GetProperty("metadata").Clone();
                trimmedResults = results.EnumerateArray().Select(result => result.Clone()).ToArray();
                trimmedBudget = budget;
                break;
            }

            Assert.NotEqual(0, trimmedBudget);
            var trimmedCursor = Assert.IsType<string>(trimmedMetadata.GetProperty("next_cursor").GetString());
            Assert.Equal(
                trimmedCursor,
                trimmedMetadata.GetProperty("stream_terminal").GetProperty("next_cursor").GetString());
            var trimmedPaths = trimmedResults
                .Select(result => result.GetProperty("path").GetString())
                .ToHashSet(StringComparer.Ordinal);
            var (trimmedReplayExitCode, trimmedReplayStdout, trimmedReplayStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    [
                        "search", "Issue5230Large", "--db", dbPath, "--exact-substring",
                        "--json", "--limit", "8", "--max-line-width", "4096",
                        "--cursor", trimmedCursor,
                    ],
                    _jsonOptions,
                    "1.0.0-test"));
            Assert.Equal(CommandExitCodes.Success, trimmedReplayExitCode);
            Assert.Equal(string.Empty, trimmedReplayStderr);
            using (var replayDocument = JsonDocument.Parse(trimmedReplayStdout))
            {
                var replayPaths = replayDocument.RootElement.GetProperty("results")
                    .EnumerateArray()
                    .Select(result => result.GetProperty("path").GetString())
                    .ToArray();
                Assert.DoesNotContain(replayPaths, path => trimmedPaths.Contains(path));
                var allPaths = trimmedPaths
                    .Concat(replayPaths)
                    .Append(firstPagePath)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Equal(10, allPaths.Count);
            }

            JsonElement budgetTerminal = default;
            JsonElement[] budgetRecords = [];
            var selectedBudget = 0;
            foreach (var budget in Enumerable.Range(0, 161).Select(index => 200 + (index * 25)))
            {
                var (exitCode, stdout, _) = CaptureConsole(() => ProgramRunner.Run(
                    [
                        "search", "Issue5230Large", "--db", dbPath, "--exact-substring",
                        "--json", "--limit", "10", "--max-line-width", "4096",
                        "--max-json-bytes", budget.ToString(),
                    ],
                    _jsonOptions,
                    "1.0.0-test"));
                if (exitCode != CommandExitCodes.PartialResult || string.IsNullOrWhiteSpace(stdout))
                    continue;

                var records = ParseNdjson(stdout);
                var candidate = records[^1];
                if (!candidate.TryGetProperty("terminal_record", out var isTerminal)
                    || !isTerminal.GetBoolean())
                {
                    continue;
                }

                Assert.True(Encoding.UTF8.GetByteCount(stdout) <= budget);
                budgetTerminal = candidate;
                budgetRecords = records;
                selectedBudget = budget;
                break;
            }

            Assert.NotEqual(0, selectedBudget);
            Assert.True(budgetTerminal.GetProperty("has_more").GetBoolean());
            var returnedCount = budgetTerminal.GetProperty("count").GetInt32();
            Assert.Equal(returnedCount, budgetRecords.Length - 1);
            if (returnedCount == 0)
            {
                Assert.False(budgetTerminal.TryGetProperty("next_cursor", out _));
                Assert.Equal(
                    "no_result_row_emitted",
                    budgetTerminal.GetProperty("next_cursor_unavailable_reason").GetString());
            }
            else
            {
                var cursor = Assert.IsType<string>(budgetTerminal.GetProperty("next_cursor").GetString());
                var emittedPaths = budgetRecords[..^1]
                    .Select(record => record.GetProperty("path").GetString())
                    .ToHashSet(StringComparer.Ordinal);
                var (resumeExitCode, resumeStdout, resumeStderr) = CaptureConsole(() =>
                    ProgramRunner.Run(
                        [
                            "search", "Issue5230Large", "--db", dbPath, "--exact-substring",
                            "--json", "--limit", "10", "--cursor", cursor,
                        ],
                        _jsonOptions,
                        "1.0.0-test"));
                Assert.Equal(CommandExitCodes.Success, resumeExitCode);
                Assert.Equal(string.Empty, resumeStderr);
                using var resumeDocument = JsonDocument.Parse(resumeStdout);
                Assert.DoesNotContain(
                    resumeDocument.RootElement.GetProperty("results").EnumerateArray(),
                    result => emittedPaths.Contains(result.GetProperty("path").GetString()));
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private void AssertRawCursorResumesWithoutRepeatingFirstPath(string[] args)
    {
        var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() =>
            ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));
        Assert.Equal(CommandExitCodes.Success, firstExitCode);
        Assert.Equal(string.Empty, firstStderr);
        var records = ParseNdjson(firstStdout);
        var firstPath = records[0].GetProperty("path").GetString();
        var terminal = records[^1];
        Assert.True(terminal.GetProperty("has_more").GetBoolean());
        var cursor = Assert.IsType<string>(terminal.GetProperty("next_cursor").GetString());
        Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);

        var (secondExitCode, secondStdout, secondStderr) = CaptureConsole(() => ProgramRunner.Run(
            args.Concat(["--cursor", cursor]).ToArray(),
            _jsonOptions,
            "1.0.0-test"));
        Assert.Equal(CommandExitCodes.Success, secondExitCode);
        Assert.Equal(string.Empty, secondStderr);
        using var secondDocument = JsonDocument.Parse(secondStdout);
        var secondPath = secondDocument.RootElement.GetProperty("results")[0]
            .GetProperty("path")
            .GetString();
        Assert.NotEqual(firstPath, secondPath);
        var metadata = secondDocument.RootElement.GetProperty("metadata");
        Assert.Equal(
            metadata.GetProperty("next_cursor").GetString(),
            metadata.GetProperty("stream_terminal").GetProperty("next_cursor").GetString());
    }

    private static JsonElement[] ParseNdjson(string stdout)
        => stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static string ReplaceResponseCursorOffset(string cursor, int offset)
    {
        const string prefix = "response:v2:";
        Assert.StartsWith(prefix, cursor, StringComparison.Ordinal);
        var encoded = cursor[prefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded += new string('=', (4 - (encoded.Length % 4)) % 4);
        var payload = JsonNode.Parse(Convert.FromBase64String(encoded))!.AsObject();
        payload["offset"] = offset;
        return prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.ToJsonString()))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);
}
