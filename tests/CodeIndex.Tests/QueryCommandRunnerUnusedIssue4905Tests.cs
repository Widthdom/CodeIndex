using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    private const int UnusedPageByteBudgetIssue4905 = 5_500;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RunUnused_MaxJsonBytesPagesWholeUnicodeRowsAndResumes_Issue4905(
        bool compact,
        bool byBucket)
    {
        var (projectRoot, dbPath) = CreateUnusedByteBudgetFixtureDbIssue4905();
        try
        {
            var legacyArgs = new List<string>
            {
                "unused", "--db", dbPath, "--json", "--all", "--lang", "csharp", "--limit", "100",
            };
            var (legacyExitCode, legacyStdout, legacyStderr) = CaptureConsole(() =>
                ProgramRunner.Run([.. legacyArgs], _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, legacyExitCode);
            Assert.Equal(string.Empty, legacyStderr);
            using var legacyDocument = JsonDocument.Parse(legacyStdout);
            Assert.False(legacyDocument.RootElement.TryGetProperty("metadata", out _));
            var expectedRows = legacyDocument.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(ReadUnusedIdentityIssue4905)
                .ToArray();
            Assert.Contains(
                legacyDocument.RootElement.GetProperty("symbols").EnumerateArray(),
                row => row.GetProperty("name").GetString() == "未使用方法00"
                       && row.GetProperty("signature").GetString()!.Contains("引数23", StringComparison.Ordinal));

            var baseArgs = new List<string>
            {
                "unused", "--db", dbPath, "--json", "--all", "--lang", "csharp",
                "--limit", "100", "--max-json-bytes", UnusedPageByteBudgetIssue4905.ToString(),
            };
            if (compact)
                baseArgs.Add("--compact");
            if (byBucket)
                baseArgs.Add("--by-bucket");

            var actualRows = new List<(string Path, int Line, string Name)>();
            string? cursor = null;
            var pageCount = 0;
            do
            {
                var args = cursor is null
                    ? baseArgs.ToArray()
                    : baseArgs.Concat(["--cursor", cursor]).ToArray();
                var (exitCode, stdout, stderr) = CaptureConsole(() =>
                    ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.True(
                    Encoding.UTF8.GetByteCount(stdout) <= UnusedPageByteBudgetIssue4905,
                    $"stdout exceeded {UnusedPageByteBudgetIssue4905} UTF-8 bytes.");
                using var document = JsonDocument.Parse(stdout);
                var root = document.RootElement;
                var metadata = root.GetProperty("metadata");
                var results = root.GetProperty("results").EnumerateArray().ToArray();

                pageCount++;
                Assert.True(pageCount <= expectedRows.Length, "unused byte-budget cursor did not make forward progress.");
                Assert.NotEmpty(results);
                Assert.Equal("unused", metadata.GetProperty("command").GetString());
                Assert.Equal("symbols", metadata.GetProperty("primary_collection").GetString());
                Assert.Equal(expectedRows.Length, metadata.GetProperty("total_count").GetInt32());
                Assert.True(metadata.GetProperty("total_count_authoritative").GetBoolean());
                Assert.Equal(results.Length, metadata.GetProperty("returned_count").GetInt32());
                Assert.Equal(
                    results.Length,
                    metadata.GetProperty("response_context").GetProperty("count").GetInt32());
                Assert.Equal(
                    results.Length,
                    metadata
                        .GetProperty("response_context")
                        .GetProperty("returned_bucket_counts")
                        .EnumerateObject()
                        .Sum(property => property.Value.GetInt32()));

                if (compact)
                {
                    Assert.Equal("compact", metadata.GetProperty("format").GetString());
                    Assert.All(results, row =>
                    {
                        Assert.False(row.TryGetProperty("signature", out _));
                        Assert.False(row.TryGetProperty("unused_reason", out _));
                        Assert.True(row.TryGetProperty("unused_bucket", out _));
                    });
                }
                else
                {
                    Assert.All(results, row => Assert.True(row.TryGetProperty("signature", out _)));
                }

                if (byBucket)
                {
                    var flattened = root.GetProperty("by_bucket")
                        .EnumerateObject()
                        .SelectMany(property => property.Value.EnumerateArray())
                        .Select(ReadUnusedIdentityIssue4905)
                        .ToArray();
                    Assert.Equal(results.Select(ReadUnusedIdentityIssue4905).Order(), flattened.Order());
                }
                else
                {
                    Assert.False(root.TryGetProperty("by_bucket", out _));
                }

                actualRows.AddRange(results.Select(ReadUnusedIdentityIssue4905));
                cursor = metadata.GetProperty("next_cursor").GetString();
                if (cursor is not null)
                    Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);
            }
            while (cursor is not null);

            Assert.True(pageCount > 1);
            Assert.Equal(expectedRows, actualRows);
            Assert.Equal(actualRows.Count, actualRows.Distinct().Count());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_MaxJsonBytesHandlesMinimumEmptyExactBoundaryAndHelp_Issue4905()
    {
        var (projectRoot, dbPath) = CreateUnusedByteBudgetFixtureDbIssue4905();
        try
        {
            var (smallExitCode, smallStdout, smallStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    [
                        "unused", "--db", dbPath, "--json", "--all", "--lang", "csharp",
                        "--max-json-bytes", "64",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, smallExitCode);
            Assert.Equal(string.Empty, smallStderr);
            using var smallDocument = JsonDocument.Parse(smallStdout);
            var smallError = smallDocument.RootElement;
            Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, smallError.GetProperty("error_code").GetString());
            Assert.Equal("response_budget", smallError.GetProperty("category").GetString());
            Assert.Equal("unused", smallError.GetProperty("command").GetString());
            Assert.Contains(
                "bounded response metadata and one projected row",
                smallError.GetProperty("message").GetString(),
                StringComparison.Ordinal);
            Assert.Equal(64, smallError.GetProperty("requested_bytes").GetInt64());

            var (emptyExitCode, emptyStdout, emptyStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    [
                        "unused", "--db", dbPath, "--json", "--all", "--lang", "csharp",
                        "--path", "does-not-exist/**",
                        "--max-json-bytes", UnusedPageByteBudgetIssue4905.ToString(),
                    ],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, emptyExitCode);
            Assert.Equal(string.Empty, emptyStderr);
            Assert.True(Encoding.UTF8.GetByteCount(emptyStdout) <= UnusedPageByteBudgetIssue4905);
            using (var emptyDocument = JsonDocument.Parse(emptyStdout))
            {
                Assert.Empty(emptyDocument.RootElement.GetProperty("results").EnumerateArray());
                Assert.Equal(0, emptyDocument.RootElement.GetProperty("metadata").GetProperty("total_count").GetInt32());
                Assert.Null(emptyDocument.RootElement.GetProperty("metadata").GetProperty("next_cursor").GetString());
            }

            const string unicodeJson = """{"results":[{"name":"未使用猫"}]}""";
            var exactBudget = Encoding.UTF8.GetByteCount(unicodeJson)
                              + Encoding.UTF8.GetByteCount(Environment.NewLine);
            Assert.True(JsonEnvelopeWrapper.JsonFitsResponseBudget(unicodeJson, exactBudget));
            Assert.False(JsonEnvelopeWrapper.JsonFitsResponseBudget(unicodeJson, exactBudget - 1));

            var flag = Assert.Single(
                CliFlagSchema.GetCompletionFlagsForCommand("unused"),
                candidate => candidate.Name == "--max-json-bytes");
            Assert.Contains("Bound emitted JSON bytes", flag.Description, StringComparison.Ordinal);
            var (printed, helpStdout, helpStderr) = CaptureConsole(() =>
                ConsoleUi.PrintCommandUsage("unused") ? 1 : 0);
            Assert.Equal(1, printed);
            Assert.Equal(string.Empty, helpStderr);
            Assert.Contains("--max-json-bytes <n>", helpStdout, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunUnused_MaxJsonBytesBindsCursorToFiltersAndIndexGeneration_Issue4905()
    {
        var (projectRoot, dbPath) = CreateUnusedByteBudgetFixtureDbIssue4905();
        try
        {
            var baseArgs = new[]
            {
                "unused", "--db", dbPath, "--json", "--all", "--lang", "csharp",
                "--limit", "1", "--max-json-bytes", "20000",
            };
            var (firstExitCode, firstStdout, firstStderr) = CaptureConsole(() =>
                ProgramRunner.Run(baseArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, firstExitCode);
            Assert.Equal(string.Empty, firstStderr);
            using var firstDocument = JsonDocument.Parse(firstStdout);
            var cursor = firstDocument.RootElement
                .GetProperty("metadata")
                .GetProperty("next_cursor")
                .GetString();
            Assert.NotNull(cursor);

            var (mismatchExitCode, mismatchStdout, mismatchStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    baseArgs.Concat(["--bucket", "likely_unused_private", "--cursor", cursor!]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, mismatchExitCode);
            Assert.Equal(string.Empty, mismatchStdout);
            Assert.Contains("cursor_mismatch", mismatchStderr, StringComparison.Ordinal);

            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/GenerationChange.cs",
                "csharp",
                "internal sealed class GenerationChange { private void NewlyUnused() { } }");
            using (var db = new DbContext(DbOpenIntent.WriteIndex, dbPath))
                new DbWriter(db.Connection).MarkGraphReady();

            var (staleExitCode, staleStdout, staleStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    baseArgs.Concat(["--cursor", cursor!]).ToArray(),
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, staleExitCode);
            Assert.Equal(string.Empty, staleStdout);
            Assert.Contains("cursor_stale", staleStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static (string ProjectRoot, string DbPath) CreateUnusedByteBudgetFixtureDbIssue4905()
    {
        var (projectRoot, dbPath) = CreateUnusedFixtureDb();
        var parameters = string.Join(
            ", ",
            Enumerable.Range(0, 24).Select(index => $"string 引数{index:D2}"));
        var methods = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 10).Select(index =>
                $"private string 未使用方法{index:D2}({parameters}) => \"値{index:D2}猫\";"));
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/UnicodeUnused.cs",
            "csharp",
            $$"""
            namespace 世界;
            internal sealed class UnicodeUnused
            {
                {{methods}}
            }
            """);
        using var db = new DbContext(DbOpenIntent.WriteIndex, dbPath);
        new DbWriter(db.Connection).MarkGraphReady();
        return (projectRoot, dbPath);
    }

    private static (string Path, int Line, string Name) ReadUnusedIdentityIssue4905(JsonElement row)
        => (
            row.GetProperty("path").GetString()!,
            row.GetProperty("line").GetInt32(),
            row.GetProperty("name").GetString()!);
}
