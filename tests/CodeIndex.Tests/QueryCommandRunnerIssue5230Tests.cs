using System.Text;
using System.Text.Json;
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

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);
}
