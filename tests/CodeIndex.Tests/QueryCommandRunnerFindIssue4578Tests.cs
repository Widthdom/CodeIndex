using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunFind_AllScopeRowsAndCountsExposeTerminalScanStateAndPartialOptIn_Issue4578()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_scan_terminal_4578");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/large.txt", "text", "alpha\nalpha\n");

            var (rowExitCode, rowStdout, rowStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json", "--line-scan-limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, rowExitCode);
            Assert.Equal(string.Empty, rowStderr);
            var rowLines = rowStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, rowLines.Length);
            using (var row = JsonDocument.Parse(rowLines[0]))
                Assert.Equal("src/large.txt", row.RootElement.GetProperty("path").GetString());
            using (var terminal = JsonDocument.Parse(rowLines[1]))
                AssertPartialFindTerminal(terminal.RootElement, countMode: false);

            var (allowedRowExitCode, allowedRowStdout, allowedRowStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json", "--line-scan-limit", "1", "--allow-partial"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, allowedRowExitCode);
            Assert.Equal(string.Empty, allowedRowStderr);
            using (var allowedTerminal = ParseLastNdjsonRecord(allowedRowStdout))
                AssertPartialFindTerminal(allowedTerminal.RootElement, countMode: false);

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json", "--count", "--line-scan-limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            using (var count = ParseJsonOutput(countStdout))
                AssertPartialFindTerminal(count.RootElement, countMode: true);

            var (allowedCountExitCode, allowedCountStdout, allowedCountStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json", "--count", "--line-scan-limit", "1", "--allow-partial"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, allowedCountExitCode);
            Assert.Equal(string.Empty, allowedCountStderr);
            using (var allowedCount = ParseJsonOutput(allowedCountStdout))
                AssertPartialFindTerminal(allowedCount.RootElement, countMode: true);

            var (completeExitCode, completeStdout, completeStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, completeExitCode);
            Assert.Equal(string.Empty, completeStderr);
            using var completeTerminal = ParseLastNdjsonRecord(completeStdout);
            var complete = completeTerminal.RootElement;
            Assert.True(complete.GetProperty("terminal_record").GetBoolean());
            Assert.True(complete.GetProperty("done").GetBoolean());
            Assert.True(complete.GetProperty("scan_complete").GetBoolean());
            Assert.True(complete.GetProperty("authoritative_rows").GetBoolean());
            Assert.False(complete.GetProperty("partial_result").GetBoolean());
            Assert.Equal(2, complete.GetProperty("returned_count").GetInt32());
            Assert.Equal(2, complete.GetProperty("lines_scanned").GetInt32());

            var (limitedExitCode, limitedStdout, limitedStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--json", "--limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, limitedExitCode);
            Assert.Equal(string.Empty, limitedStderr);
            using var limitedTerminal = ParseLastNdjsonRecord(limitedStdout);
            var limited = limitedTerminal.RootElement;
            Assert.False(limited.GetProperty("done").GetBoolean());
            Assert.False(limited.GetProperty("scan_complete").GetBoolean());
            Assert.False(limited.GetProperty("authoritative_rows").GetBoolean());
            Assert.False(limited.GetProperty("partial_result").GetBoolean());
            Assert.True(limited.GetProperty("result_limit_reached").GetBoolean());
            Assert.Equal("limit", limited.GetProperty("truncation_reason").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_AllScopeRejectsRowFormatsThatCannotCarryScanMetadata_Issue4578()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_scan_formats_4578");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/match.txt", "text", "alpha\n");

            var formatArguments = new[]
            {
                new[] { "--json=array" },
                new[] { "--format", "compact" },
                new[] { "--format", "csv" },
                new[] { "--format", "tsv" },
                new[] { "--format", "lsp" },
                new[] { "--format", "qf" },
                new[] { "--format", "sarif" },
                new[] { "--json", "--format", "text" },
                new[] { "--json=array", "--format", "text" },
                new[] { "--format", "text", "--json=array" },
            };

            foreach (var formatArgs in formatArguments)
            {
                var args = new List<string> { "alpha", "--db", dbPath, "--all" };
                args.AddRange(formatArgs);
                var (exitCode, stdout, stderr) = CaptureConsole(() =>
                    QueryCommandRunner.RunFind([.. args], _jsonOptions));

                Assert.Equal(CommandExitCodes.UsageError, exitCode);
                Assert.Equal(string.Empty, stdout);
                Assert.Contains("streaming NDJSON", stderr, StringComparison.Ordinal);
            }

            var (normalizedExitCode, normalizedStdout, normalizedStderr) = CaptureConsole(() =>
                QueryCommandRunner.RunFind(
                    ["alpha", "--db", dbPath, "--all", "--format", "text", "--json"],
                    _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, normalizedExitCode);
            Assert.Equal(string.Empty, normalizedStderr);
            using var normalizedTerminal = ParseLastNdjsonRecord(normalizedStdout);
            Assert.True(normalizedTerminal.RootElement.GetProperty("terminal_record").GetBoolean());
            Assert.True(normalizedTerminal.RootElement.GetProperty("scan_complete").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_AllScopeJsonEnvelopeKeepsTerminalOutOfRows_Issue4578()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_envelope_terminal_4578");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/large.txt", "text", "alpha\nalpha\n");

            var (exitCode, stdout, stderr) = CaptureConsole(() => ProgramRunner.Run(
                ["find", "alpha", "--db", dbPath, "--all", "--json-envelope", "--line-scan-limit", "1"],
                _jsonOptions,
                "test"));

            Assert.Equal(CommandExitCodes.PartialResult, exitCode);
            Assert.Equal(string.Empty, stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("results").GetArrayLength());
            var metadata = root.GetProperty("metadata");
            Assert.Equal(1, metadata.GetProperty("result_count").GetInt32());
            var terminal = metadata.GetProperty("stream_terminal");
            Assert.True(terminal.GetProperty("terminal_record").GetBoolean());
            Assert.True(terminal.GetProperty("partial_result").GetBoolean());
            Assert.False(terminal.GetProperty("authoritative_rows").GetBoolean());
            Assert.Equal("line_scan_limit", terminal.GetProperty("scan_truncation_reason").GetString());

            var (countExitCode, countStdout, countStderr) = CaptureConsole(() => ProgramRunner.Run(
                ["find", "alpha", "--db", dbPath, "--all", "--count", "--json-envelope", "--line-scan-limit", "1"],
                _jsonOptions,
                "test"));

            Assert.Equal(CommandExitCodes.PartialResult, countExitCode);
            Assert.Equal(string.Empty, countStderr);
            using var countDocument = JsonDocument.Parse(countStdout);
            var countRoot = countDocument.RootElement;
            Assert.Single(countRoot.GetProperty("results").EnumerateArray());
            Assert.Equal(1, countRoot.GetProperty("metadata").GetProperty("result_count").GetInt32());
            Assert.Equal(1, countRoot.GetProperty("results")[0].GetProperty("count").GetInt32());
            Assert.True(countRoot.GetProperty("metadata").GetProperty("stream_terminal").GetProperty("terminal_record").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunFind_AllScopeTextSummaryCarriesAuthorityCapsAndRecovery_Issue4578()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_find_text_summary_4578");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(dbPath, "src/large.txt", "text", "alpha\nalpha\n");

            var (rowExitCode, _, rowStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--line-scan-limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, rowExitCode);
            Assert.Contains("candidate_file_limit=", rowStderr, StringComparison.Ordinal);
            Assert.Contains("line_scan_limit=1", rowStderr, StringComparison.Ordinal);
            Assert.Contains("scan_complete=false", rowStderr, StringComparison.Ordinal);
            Assert.Contains("authoritative_rows=false", rowStderr, StringComparison.Ordinal);
            Assert.Contains("continuation_action=resume_with_next_cursor", rowStderr, StringComparison.Ordinal);
            Assert.Contains("next_cursor=response:v2:", rowStderr, StringComparison.Ordinal);

            var (countExitCode, _, countStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--count", "--line-scan-limit", "1"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.PartialResult, countExitCode);
            Assert.Contains("authoritative_count=false", countStderr, StringComparison.Ordinal);
            Assert.Contains("continuation_action=resume_with_next_cursor", countStderr, StringComparison.Ordinal);
            Assert.Contains("next_cursor=response:v2:", countStderr, StringComparison.Ordinal);

            var (completeExitCode, _, completeStderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
                ["alpha", "--db", dbPath, "--all", "--limit", "10"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, completeExitCode);
            Assert.Contains("scan_complete=true", completeStderr, StringComparison.Ordinal);
            Assert.Contains("authoritative_rows=true", completeStderr, StringComparison.Ordinal);
            Assert.Contains("line_scan_limit=", completeStderr, StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static void AssertPartialFindTerminal(JsonElement json, bool countMode)
    {
        Assert.True(json.GetProperty("terminal_record").GetBoolean());
        Assert.False(json.GetProperty("done").GetBoolean());
        Assert.True(json.GetProperty("partial_result").GetBoolean());
        Assert.False(json.GetProperty("scan_complete").GetBoolean());
        Assert.True(json.GetProperty("has_more").GetBoolean());
        Assert.Equal(1, json.GetProperty("returned_count").GetInt32());
        Assert.Equal(1, json.GetProperty("candidate_files").GetInt32());
        Assert.Equal(1, json.GetProperty("files_scanned").GetInt32());
        Assert.Equal(1, json.GetProperty("lines_scanned").GetInt32());
        Assert.True(json.GetProperty("scan_truncated").GetBoolean());
        Assert.True(json.GetProperty("scan_cap_reached").GetBoolean());
        Assert.Equal("line_scan_limit", json.GetProperty("scan_truncation_reason").GetString());
        Assert.Equal("line_scan_limit", json.GetProperty("truncation_reason").GetString());
        Assert.Equal(QueryCommandRunner.FindAllCandidateFileLimit, json.GetProperty("candidate_file_limit").GetInt32());
        Assert.Equal(1, json.GetProperty("line_scan_limit").GetInt32());
        if (countMode)
        {
            Assert.Equal("resume_with_next_cursor", json.GetProperty("continuation_action").GetString());
            Assert.Contains("--cursor", json.GetProperty("recovery_guidance").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("response:v2:", json.GetProperty("next_cursor").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("result_stable_at").GetString()));
            Assert.False(json.GetProperty("authoritative_count").GetBoolean());
        }
        else
        {
            Assert.Equal("resume_with_next_cursor", json.GetProperty("continuation_action").GetString());
            Assert.StartsWith("response:v2:", json.GetProperty("next_cursor").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("result_stable_at").GetString()));
            Assert.False(json.GetProperty("authoritative_rows").GetBoolean());
            Assert.Equal(20, json.GetProperty("applied_limit").GetInt32());
        }
    }
}
