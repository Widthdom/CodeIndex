using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunValidate_IndexedIssueViewsShareSupersetFixture_Issues1582_2992_3010_3896_3897_4908()
    {
        const string primaryBomPath = "src/App.cs";
        const string cleanPath = "src/clean.cs";
        const string excludedRoot = "src/excluded";
        const string mixedPath = "src/excluded/mixed.cs";

        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_validate_views");
        var projectRoot = project.Root;
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        WriteUtf8BomFile(projectRoot, primaryBomPath, "class App {}\n");
        TestProjectHelper.WriteTextFile(projectRoot, cleanPath, "class Clean {}\n");
        WriteUtf8BomFile(projectRoot, "src/excluded/Excluded.cs", "class Excluded {}\n");
        TestProjectHelper.WriteTextFile(projectRoot, mixedPath, "class Mixed {}\r\nclass Other {}\n");
        WriteUtf8BomFile(projectRoot, "tests/AppTests.cs", "class AppTests {}\n");

        var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
            [projectRoot, "--db", dbPath, "--json", "--quiet"],
            _jsonOptions));
        Assert.Equal(CommandExitCodes.Success, indexExitCode);
        Assert.Equal(string.Empty, indexStderr);

        (int ExitCode, string Stdout, string Stderr) RunValidate(params string[] args)
            => CaptureConsole(() => QueryCommandRunner.RunValidate(["--db", dbPath, .. args], _jsonOptions));

        // Both pagination aliases cap returned rows without changing the command contract (#2992).
        var (limitExitCode, limitStdout, limitStderr) = RunValidate("--json", "--limit", "1");
        var (topExitCode, topStdout, topStderr) = RunValidate("--json", "--top", "1");

        using var limitDocument = ParseJsonOutput(limitStdout);
        using var topDocument = ParseJsonOutput(topStdout);

        Assert.Equal(CommandExitCodes.Success, limitExitCode);
        Assert.Equal(CommandExitCodes.Success, topExitCode);
        Assert.Equal(string.Empty, limitStderr);
        Assert.Equal(string.Empty, topStderr);
        Assert.Equal(1, limitDocument.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, limitDocument.RootElement.GetProperty("issues").GetArrayLength());
        Assert.Equal(1, topDocument.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(1, topDocument.RootElement.GetProperty("issues").GetArrayLength());

        // The array projection returns the first deterministic issue with its persisted metadata (#3010).
        var (arrayExitCode, arrayStdout, arrayStderr) = RunValidate("--json=array", "--limit", "1");
        using var arrayDocument = ParseJsonOutput(arrayStdout);
        var arrayRoot = arrayDocument.RootElement;
        Assert.Equal(CommandExitCodes.Success, arrayExitCode);
        Assert.Equal(string.Empty, arrayStderr);
        Assert.Equal(JsonValueKind.Array, arrayRoot.ValueKind);
        Assert.Equal(1, arrayRoot.GetArrayLength());
        Assert.Equal("bom", arrayRoot[0].GetProperty("kind").GetString());
        Assert.Equal(FileIssue.OriginByteOrderMark, arrayRoot[0].GetProperty("origin").GetString());
        Assert.Equal(FileIssue.SeverityWarning, arrayRoot[0].GetProperty("severity").GetString());

        // A path-scoped clean file exercises the empty-array branch without rebuilding the index (#3010).
        var (emptyExitCode, emptyStdout, emptyStderr) = RunValidate("--json=array", "--path", cleanPath);
        using var emptyDocument = ParseJsonOutput(emptyStdout);
        var emptyRoot = emptyDocument.RootElement;
        Assert.Equal(CommandExitCodes.Success, emptyExitCode);
        Assert.Equal(string.Empty, emptyStderr);
        Assert.Equal(JsonValueKind.Array, emptyRoot.ValueKind);
        Assert.Empty(emptyRoot.EnumerateArray());

        // A trailing --json must retain the count envelope selected by --format count (#3896, #4908).
        var (countExitCode, countStdout, countStderr) = RunValidate(
            "--path", primaryBomPath, "--format", "count", "--json");
        Assert.Equal(CommandExitCodes.Success, countExitCode);
        Assert.Equal(string.Empty, countStderr);
        using var countDocument = ParseJsonOutput(countStdout);
        var countRoot = countDocument.RootElement;
        Assert.Equal(1, countRoot.GetProperty("count").GetInt32());
        Assert.Equal(1, countRoot.GetProperty("total_estimated").GetInt32());
        Assert.Equal(JsonOutputContract.ApiVersion, countRoot.GetProperty("api_version").GetString());
        Assert.Equal("validation_issues", countRoot.GetProperty("count_kind").GetString());
        Assert.Equal("all_matching_issues_before_limit", countRoot.GetProperty("count_scope").GetString());
        Assert.True(countRoot.GetProperty("authoritative_count").GetBoolean());
        Assert.False(countRoot.TryGetProperty("issues", out _));

        // Scope one BOM and one mixed-line-ending issue, then prove --kind narrows to the BOM.
        var (kindExitCode, kindStdout, kindStderr) = RunValidate(
            "--json", "--path", primaryBomPath, "--path", mixedPath, "--kind", "bom");
        using var kindDocument = ParseJsonOutput(kindStdout);
        var kindRoot = kindDocument.RootElement;
        Assert.Equal(CommandExitCodes.Success, kindExitCode);
        Assert.Equal(string.Empty, kindStderr);
        Assert.Equal(1, kindRoot.GetProperty("count").GetInt32());
        Assert.Equal("bom", kindRoot.GetProperty("issues")[0].GetProperty("kind").GetString());
        Assert.Equal(FileIssue.OriginByteOrderMark, kindRoot.GetProperty("issues")[0].GetProperty("origin").GetString());
        Assert.Equal(FileIssue.SeverityWarning, kindRoot.GetProperty("issues")[0].GetProperty("severity").GetString());

        // `validate --kind replacement_chra` previously filtered the file_issues table by an
        // unknown kind, returned zero rows, and printed the same "No encoding issues found."
        // message a genuinely-clean repo would print — silently masking the typo. Round-2 adds
        // a known-kind allowlist + did-you-mean hint (#1582).
        // 従来 `validate --kind replacement_chra` は file_issues を 0 行に絞り込み、本当に
        // クリーンな状態と同じ "No encoding issues found." を出して typo を握り潰していた。
        // round-2 で許可された kind 一覧と did-you-mean を追加した (#1582)。
        var (typoExitCode, _, typoStderr) = RunValidate("--kind", "replacement_chra");
        Assert.Equal(CommandExitCodes.Success, typoExitCode);
        Assert.Contains("No encoding issues found.", typoStderr);
        Assert.Contains("'replacement_chra' is not a known validate kind", typoStderr);
        Assert.Contains("Did you mean: --kind replacement_char?", typoStderr);

        // Test and explicit path exclusions leave only the primary BOM issue (#3897).
        var (excludeExitCode, excludeStdout, excludeStderr) = RunValidate(
            "--json", "--exclude-tests", "--exclude-path", excludedRoot);
        using var excludeDocument = ParseJsonOutput(excludeStdout);
        var excludeRoot = excludeDocument.RootElement;
        var excludeIssues = excludeRoot.GetProperty("issues");
        Assert.Equal(CommandExitCodes.Success, excludeExitCode);
        Assert.Equal(string.Empty, excludeStderr);
        Assert.Equal(1, excludeRoot.GetProperty("count").GetInt32());
        Assert.Equal(primaryBomPath, excludeIssues[0].GetProperty("path").GetString());
        Assert.Equal("bom", excludeIssues[0].GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("--limit")]
    [InlineData("--top")]
    public void RunValidate_InvalidLimitOrTopReturnsUsageError_Issue2992(string flag)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            [flag, "nope"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("requires an integer between 1 and 10000", stderr);
        Assert.Contains("got 'nope'", stderr);
        Assert.Contains($"Usage: {ConsoleUi.GetUsageLine("validate")}", stderr);
        Assert.DoesNotContain("is not supported for validate", stderr);
        Assert.DoesNotContain("database not found", stderr);
    }

    [Fact]
    public void RunValidate_InvalidSeverityJsonReturnsStructuredError_Issue3896()
    {
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
            ["--severity", "invalid", "--json"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = ParseJsonOutput(stdout);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("unsupported validate severity 'invalid'.", document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void ValidateContent_SuppressesSolutionUtf8BomNoise_Issue3897()
    {
        var rawBytes = new byte[] { 0xEF, 0xBB, 0xBF, 0x63, 0x6C, 0x61, 0x73, 0x73 };

        var solutionIssues = FileIndexer.ValidateContent(
            "CodeIndex.sln",
            rawBytes,
            "\uFEFFMicrosoft Visual Studio Solution File, Format Version 12.00\n",
            "text");
        var csharpIssues = FileIndexer.ValidateContent(
            "src/Bom.cs",
            rawBytes,
            "\uFEFFclass Bom {}\n",
            "csharp");

        Assert.DoesNotContain(solutionIssues, issue => issue.Kind == "bom");
        Assert.Contains(csharpIssues, issue => issue.Kind == "bom");
    }

    private static void WriteUtf8BomFile(string projectRoot, string relativePath, string content)
        => TestProjectHelper.WriteBinaryFile(projectRoot, relativePath, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(content)]);
}
