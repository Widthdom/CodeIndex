using System.Text;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunSearch_NdjsonHardCapIncludesTerminalAndRequiresExplicitPartialOptIn_Issue4561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_ndjson_hard_cap_4561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/large.cs",
                "csharp",
                $"public class Issue4561Large {{ void Run() {{ Issue4561Needle(); }} }} // {new string('x', 2_000)}\n");

            var (tinyExitCode, tinyStdout, tinyStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue4561Needle", "--db", dbPath, "--json=ndjson", "--max-json-bytes", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.UsageError, tinyExitCode);
            Assert.Equal(string.Empty, tinyStdout);
            Assert.Contains("terminal record", tinyStderr, StringComparison.Ordinal);

            var (partialExitCode, partialStdout, partialStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue4561Needle", "--db", dbPath, "--json=ndjson", "--max-json-bytes", "600"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal(string.Empty, partialStderr);
            Assert.True(Encoding.UTF8.GetByteCount(partialStdout) <= 600);
            using (var terminal = ParseLastNdjsonRecord(partialStdout))
            {
                var json = terminal.RootElement;
                Assert.True(json.GetProperty("terminal_record").GetBoolean());
                Assert.False(json.GetProperty("done").GetBoolean());
                Assert.True(json.GetProperty("interrupted").GetBoolean());
                Assert.Equal("max_json_bytes_exceeded", json.GetProperty("truncation_reason").GetString());
                Assert.True(json.GetProperty("omitted_count").GetInt32() > 0);
            }

            var (allowedExitCode, allowedStdout, allowedStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue4561Needle", "--db", dbPath, "--json=ndjson", "--max-json-bytes", "600", "--allow-partial"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, allowedExitCode);
            Assert.Equal(string.Empty, allowedStderr);
            Assert.Equal(partialStdout, allowedStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSymbolsAndFiles_NdjsonAlwaysEndsWithTerminalMetadata_Issue4561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_discovery_terminal_4561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Issue4561Alpha.cs", "csharp", "public class Issue4561Alpha {}\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/Issue4561Bravo.cs", "csharp", "public class Issue4561Bravo {}\n");

            var commands = new (string Name, Func<int> Run)[]
            {
                ("symbols", () => QueryCommandRunner.RunSymbols(["Issue4561", "--db", dbPath, "--json", "--limit", "1"], _jsonOptions)),
                ("files", () => QueryCommandRunner.RunFiles(["Issue4561", "--db", dbPath, "--json", "--limit", "1"], _jsonOptions)),
            };

            foreach (var command in commands)
            {
                var (exitCode, stdout, stderr) = CaptureConsole(command.Run);
                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(2, lines.Length);
                using var terminal = JsonDocument.Parse(lines[^1]);
                var json = terminal.RootElement;
                Assert.True(json.GetProperty("terminal_record").GetBoolean());
                Assert.True(json.GetProperty("done").GetBoolean());
                Assert.Equal(1, json.GetProperty("count").GetInt32());
                Assert.Equal(2, json.GetProperty("total_count").GetInt32());
                Assert.True(json.GetProperty("truncated").GetBoolean());
                Assert.True(json.GetProperty("has_more").GetBoolean());
                Assert.Equal("limit", json.GetProperty("truncation_reason").GetString());
                Assert.Equal(1, json.GetProperty("applied_limit").GetInt32());
                Assert.Equal(1, json.GetProperty("omitted_count").GetInt32());
                Assert.Contains("Increase --limit", json.GetProperty("recovery_guidance").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeNdjsonUsesSharedTerminalAndPartialExitContract_Issue4561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_recipe_stream_terminal_4561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/recipe.cs",
                "csharp",
                $"try {{ Work(); }} catch (Exception ex) {{ Console.WriteLine(ex.Message); }} // {new string('x', 2_000)}\n");
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/recipe-second.cs",
                "csharp",
                "try { Work(); } catch (Exception ex) { Log(ex.Message); }\n");

            var recipeArgs = new[]
            {
                "--recipe", "risky-code/raw-diagnostic-echo",
                "--db", dbPath,
                "--json=ndjson",
                "--limit", "1",
            };
            var (completeExitCode, completeStdout, completeStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunSearch(recipeArgs, _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, completeExitCode);
            Assert.Equal(string.Empty, completeStderr);
            var completeLines = completeStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, completeLines.Length);
            using (var row = JsonDocument.Parse(completeLines[0]))
            {
                Assert.Equal("risky-code", row.RootElement.GetProperty("recipe").GetString());
                Assert.Equal("raw-diagnostic-echo", row.RootElement.GetProperty("query_name").GetString());
            }
            using (var terminal = JsonDocument.Parse(completeLines[1]))
            {
                Assert.True(terminal.RootElement.GetProperty("terminal_record").GetBoolean());
                Assert.True(terminal.RootElement.GetProperty("done").GetBoolean());
                Assert.Equal(1, terminal.RootElement.GetProperty("count").GetInt32());
                Assert.True(terminal.RootElement.GetProperty("truncated").GetBoolean());
                Assert.True(terminal.RootElement.GetProperty("has_more").GetBoolean());
                Assert.Equal("limit", terminal.RootElement.GetProperty("truncation_reason").GetString());
                Assert.True(terminal.RootElement.GetProperty("total_count_lower_bound").GetInt32() >= 2);
            }

            var (emptyExitCode, emptyStdout, emptyStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [.. recipeArgs, "--path", "does-not-match/**"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, emptyExitCode);
            Assert.Equal(string.Empty, emptyStderr);
            using (var terminal = ParseLastNdjsonRecord(emptyStdout))
            {
                Assert.Equal(0, terminal.RootElement.GetProperty("count").GetInt32());
                Assert.True(terminal.RootElement.GetProperty("terminal_record").GetBoolean());
            }

            string[] cappedArgs = [.. recipeArgs, "--max-json-bytes", "650"];
            var (partialExitCode, partialStdout, partialStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunSearch(cappedArgs, _jsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, partialExitCode);
            Assert.Equal(string.Empty, partialStderr);
            Assert.True(Encoding.UTF8.GetByteCount(partialStdout) <= 650);
            using (var terminal = ParseLastNdjsonRecord(partialStdout))
            {
                Assert.False(terminal.RootElement.GetProperty("done").GetBoolean());
                Assert.Equal("max_json_bytes_exceeded", terminal.RootElement.GetProperty("truncation_reason").GetString());
            }

            var (allowedExitCode, allowedStdout, allowedStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                [.. cappedArgs, "--allow-partial"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, allowedExitCode);
            Assert.Equal(string.Empty, allowedStderr);
            Assert.Equal(partialStdout, allowedStdout);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunSearch_RecipeTotalLimitReportsKnownOmissions_Issue4561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_recipe_total_limit_4561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/recipe.cs",
                "csharp",
                "var doc = JsonDocument.Parse(payload); var text = reader.ReadToEnd();\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["--recipe", "risky-code", "--db", dbPath, "--json=ndjson", "--limit", "10", "--total-limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var terminal = ParseLastNdjsonRecord(stdout);
            var json = terminal.RootElement;
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.True(json.GetProperty("truncated").GetBoolean());
            Assert.True(json.GetProperty("has_more").GetBoolean());
            Assert.Equal("limit", json.GetProperty("truncation_reason").GetString());
            Assert.True(json.GetProperty("total_count_lower_bound").GetInt32() >= 2);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void BoundedQueryOutputRejectsUnbudgetedDiagnosticsAndEnvelope_Issue4561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_bounded_diagnostics_4561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/needle.cs", "csharp", "public class Issue4561Needle {}\n");

            foreach (var diagnosticFlag in new[] { "--profile", "--verbose" })
            {
                var commands = new Func<int>[]
                {
                    () => QueryCommandRunner.RunSearch(
                        ["Issue4561Needle", "--db", dbPath, "--json", "--max-json-bytes", "650", diagnosticFlag],
                        _jsonOptions),
                    () => QueryCommandRunner.RunSymbols(
                        ["Issue4561Needle", "--db", dbPath, "--json", "--max-json-bytes", "650", diagnosticFlag],
                        _jsonOptions),
                    () => QueryCommandRunner.RunFiles(
                        ["needle", "--db", dbPath, "--json", "--max-json-bytes", "650", diagnosticFlag],
                        _jsonOptions),
                };

                foreach (var command in commands)
                {
                    var (exitCode, stdout, stderr) = CaptureConsole(command);
                    Assert.Equal(CommandExitCodes.UsageError, exitCode);
                    Assert.Equal(string.Empty, stdout);
                    Assert.Contains("--max-json-bytes cannot be combined", stderr, StringComparison.Ordinal);
                }
            }

            var (envelopeExitCode, envelopeStdout, envelopeStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Issue4561Needle", "--db", dbPath, "--json-envelope", "--max-json-bytes", "650"],
                _jsonOptions,
                "test"));

            Assert.Equal(CommandExitCodes.UsageError, envelopeExitCode);
            Assert.Equal(string.Empty, envelopeStdout);
            Assert.Contains("--json-envelope cannot be combined", envelopeStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void SearchTerminalMarksBoundedTotalsAndSelectionOmissions_Issue4561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_search_lower_bound_4561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/a.cs", "csharp", "Issue4561Sample();\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/b.cs", "csharp", "Issue4561Sample();\n");
            TestProjectHelper.InsertIndexedFile(dbPath, "src/c.cs", "csharp", "Issue4561Sample();\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue4561Sample", "--db", dbPath, "--exact-substring", "--json", "--sample", "1", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var terminal = ParseLastNdjsonRecord(stdout);
            var json = terminal.RootElement;
            Assert.False(json.GetProperty("truncated").GetBoolean());
            Assert.False(json.GetProperty("has_more").GetBoolean());
            Assert.Equal("sample", json.GetProperty("selection_reason").GetString());
            Assert.Equal(2, json.GetProperty("selection_omitted_count").GetInt32());
            Assert.False(json.GetProperty("total_count_authoritative").GetBoolean());
            Assert.Equal(3, json.GetProperty("total_count_lower_bound").GetInt32());

            var (combinedExitCode, combinedStdout, combinedStderr) = CaptureConsole(() => QueryCommandRunner.RunSearch(
                ["Issue4561Sample", "--db", dbPath, "--exact-substring", "--json", "--sample", "2", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, combinedExitCode);
            Assert.Equal(string.Empty, combinedStderr);
            using var combinedTerminal = ParseLastNdjsonRecord(combinedStdout);
            var combined = combinedTerminal.RootElement;
            Assert.True(combined.GetProperty("truncated").GetBoolean());
            Assert.Equal("sample", combined.GetProperty("selection_reason").GetString());
            Assert.Equal(1, combined.GetProperty("selection_omitted_count").GetInt32());
            Assert.Equal(2, combined.GetProperty("omitted_count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void JsonEnvelopeMovesStreamTerminalIntoMetadata_Issue4561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_envelope_terminal_4561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/needle.cs", "csharp", "Issue4561Envelope();\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["search", "Issue4561Envelope", "--db", dbPath, "--json-envelope"],
                _jsonOptions,
                "test"));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("results").GetArrayLength());
            var metadata = root.GetProperty("metadata");
            Assert.Equal(1, metadata.GetProperty("result_count").GetInt32());
            var terminal = metadata.GetProperty("stream_terminal");
            Assert.True(terminal.GetProperty("terminal_record").GetBoolean());
            Assert.Equal(1, terminal.GetProperty("count").GetInt32());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void JsonEnvelopeSeparatesZeroResultControlRecordsFromRows_Issue4561()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_envelope_controls_4561");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/needle.cs", "csharp", "Issue4561Envelope();\n");

            foreach (var args in new[]
            {
                new[] { "search", "NoSuchIssue4561Match", "--db", dbPath, "--json-envelope" },
                new[] { "files", "--path", "NoSuchIssue4561Path", "--db", dbPath, "--json-envelope" },
            })
            {
                var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(args, _jsonOptions, "test"));

                Assert.Equal(CommandExitCodes.Success, exitCode);
                Assert.Equal(string.Empty, stderr);
                using var document = JsonDocument.Parse(stdout);
                var root = document.RootElement;
                Assert.Equal(0, root.GetProperty("results").GetArrayLength());
                var metadata = root.GetProperty("metadata");
                Assert.Equal(0, metadata.GetProperty("result_count").GetInt32());
                Assert.Single(metadata.GetProperty("stream_control_records").EnumerateArray());
                Assert.True(metadata.GetProperty("stream_terminal").GetProperty("terminal_record").GetBoolean());
            }
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static JsonDocument ParseLastNdjsonRecord(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        return JsonDocument.Parse(lines[^1]);
    }

    private static void AssertEmptyDiscoveryTerminal(string stdout)
    {
        using var terminal = ParseLastNdjsonRecord(stdout);
        var json = terminal.RootElement;
        Assert.True(json.GetProperty("terminal_record").GetBoolean());
        Assert.True(json.GetProperty("done").GetBoolean());
        Assert.Equal(0, json.GetProperty("count").GetInt32());
        Assert.Equal(0, json.GetProperty("total_count").GetInt32());
        Assert.False(json.GetProperty("truncated").GetBoolean());
        Assert.False(json.GetProperty("has_more").GetBoolean());
    }
}
