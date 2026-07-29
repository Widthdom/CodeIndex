using System.Text;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerOutlineIssue4880Tests
{
    private const int PageByteBudget = 3_000;
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Fact]
    public void Outline_MaxJsonBytesPagesWholeUnicodeRowsAndResumesHierarchy_Issue4880()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("outline_byte_budget_4880");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            var parameters = string.Join(
                ", ",
                Enumerable.Range(0, 16).Select(index => $"string 引数{index:D2}"));
            var methods = string.Join(
                Environment.NewLine,
                Enumerable.Range(0, 12).Select(index =>
                    $"public string 方法{index:D2}({parameters}) => \"値{index:D2}猫\";"));
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/UnicodeTree.cs",
                "csharp",
                $$"""
                namespace 世界;
                public sealed class Root
                {
                    public sealed class LevelOne
                    {
                        public sealed class LevelTwo
                        {
                            {{methods}}
                        }
                    }
                }
                """);

            var (legacyExitCode, legacyStdout, legacyStderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    ["outline", "src/UnicodeTree.cs", "--db", dbPath, "--json"],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, legacyExitCode);
            Assert.Equal(string.Empty, legacyStderr);
            using var legacyDocument = JsonDocument.Parse(legacyStdout);
            Assert.False(legacyDocument.RootElement.TryGetProperty("metadata", out _));
            var expectedRows = legacyDocument.RootElement
                .GetProperty("symbols")
                .EnumerateArray()
                .Select(ReadOutlineIdentity)
                .ToArray();
            Assert.Contains(expectedRows, row => row.Name == "方法00" && row.Depth >= 3);
            Assert.Contains(
                legacyDocument.RootElement.GetProperty("symbols").EnumerateArray(),
                row => row.GetProperty("name").GetString() == "方法00"
                       && row.GetProperty("signature").GetString()!.Contains("引数15", StringComparison.Ordinal));

            var baseArgs = new[]
            {
                "outline", "src/UnicodeTree.cs", "--db", dbPath, "--json",
                "--limit", "50", "--max-json-bytes", PageByteBudget.ToString(),
            };
            var actualRows = new List<(string Name, int Depth)>();
            string? cursor = null;
            var pageCount = 0;
            do
            {
                var args = cursor is null ? baseArgs : baseArgs.Concat(["--cursor", cursor]).ToArray();
                var (exitCode, stdout, stderr) = ConsoleCapture.Capture(() =>
                    ProgramRunner.Run(args, _jsonOptions, "1.0.0-test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                Assert.True(Encoding.UTF8.GetByteCount(stdout) <= PageByteBudget);
                using var document = JsonDocument.Parse(stdout);
                var root = document.RootElement;
                var metadata = root.GetProperty("metadata");
                var results = root.GetProperty("results").EnumerateArray().ToArray();

                pageCount++;
                Assert.True(pageCount <= expectedRows.Length, "outline byte-budget cursor did not make forward progress.");
                Assert.Equal("outline", metadata.GetProperty("command").GetString());
                Assert.Equal("symbols", metadata.GetProperty("primary_collection").GetString());
                Assert.Equal(expectedRows.Length, metadata.GetProperty("total_count").GetInt32());
                Assert.True(metadata.GetProperty("total_count_authoritative").GetBoolean());
                Assert.Equal(results.Length, metadata.GetProperty("returned_count").GetInt32());
                Assert.NotEmpty(results);
                Assert.Equal(
                    "src/UnicodeTree.cs",
                    metadata.GetProperty("response_context").GetProperty("path").GetString());
                Assert.Equal(
                    expectedRows.Length,
                    metadata.GetProperty("response_context").GetProperty("total_symbol_count").GetInt32());
                Assert.False(metadata.GetProperty("response_context").TryGetProperty("next_cursor", out _));

                actualRows.AddRange(results.Select(ReadOutlineIdentity));
                cursor = metadata.GetProperty("next_cursor").GetString();
                if (cursor is not null)
                    Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);
            }
            while (cursor is not null);

            Assert.True(pageCount > 1);
            Assert.Equal(expectedRows, actualRows);
            Assert.Equal(actualRows.Count, actualRows.Distinct().Count());

            var (smallExitCode, smallStdout, smallStderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    [
                        "outline", "src/UnicodeTree.cs", "--db", dbPath, "--json",
                        "--kind", "function", "--outline-fields", "name,signature",
                        "--max-json-bytes", "64",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, smallExitCode);
            Assert.Equal(string.Empty, smallStdout);
            Assert.Contains($"Error [{CommandErrorCodes.UsageError}]", smallStderr, StringComparison.Ordinal);
            Assert.Contains("bounded response metadata and one projected row", smallStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void Outline_JsonByteBudgetCountsUnicodeAndFinalNewlineAtExactBoundary_Issue4880()
    {
        const string json = """{"results":[{"name":"方法猫"}]}""";
        var exactBudget = Encoding.UTF8.GetByteCount(json)
                          + Encoding.UTF8.GetByteCount(Environment.NewLine);

        Assert.True(JsonEnvelopeWrapper.JsonFitsResponseBudget(json, exactBudget));
        Assert.False(JsonEnvelopeWrapper.JsonFitsResponseBudget(json, exactBudget - 1));
    }

    [Fact]
    public void Outline_MaxJsonBytesIsExposedByHelpAndFlagSchema_Issue4880()
    {
        var flag = Assert.Single(
            CliFlagSchema.GetCompletionFlagsForCommand("outline"),
            candidate => candidate.Name == "--max-json-bytes");
        Assert.Contains("Bound emitted JSON bytes", flag.Description, StringComparison.Ordinal);

        var (printed, stdout, stderr) = ConsoleCapture.Capture(() =>
            ConsoleUi.PrintCommandUsage("outline") ? 1 : 0);
        Assert.Equal(1, printed);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("--max-json-bytes <n>", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Outline_MaxJsonBytesPreservesUnsupportedOutputSelectorValidation_Issue4880()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("outline_byte_budget_validation_4880");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Sample.cs",
                "csharp",
                "public sealed class Sample { }");

            var (formatExitCode, formatStdout, formatStderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    [
                        "outline", "src/Sample.cs", "--db", dbPath,
                        "--max-json-bytes", PageByteBudget.ToString(),
                        "--format", "nonsense",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, formatExitCode);
            Assert.Equal(string.Empty, formatStdout);
            Assert.Contains("--format is not supported for outline", formatStderr, StringComparison.Ordinal);

            var (jsonExitCode, jsonStdout, jsonStderr) = ConsoleCapture.Capture(() =>
                ProgramRunner.Run(
                    [
                        "outline", "src/Sample.cs", "--db", dbPath,
                        "--max-json-bytes", PageByteBudget.ToString(),
                        "--json=nonsense",
                    ],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.UsageError, jsonExitCode);
            Assert.Equal(string.Empty, jsonStderr);
            using var document = JsonDocument.Parse(jsonStdout);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
            Assert.Contains(
                "--json=<format> is not supported by outline",
                document.RootElement.GetProperty("message").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static (string Name, int Depth) ReadOutlineIdentity(JsonElement row)
        => (row.GetProperty("name").GetString()!, row.GetProperty("depth").GetInt32());
}
