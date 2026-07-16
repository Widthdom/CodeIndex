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
