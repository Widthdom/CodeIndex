using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Fact]
    public void RunValidate_LimitAndTopCapReturnedIssues_Issue2992()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_validate_limit");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllBytes(
                Path.Combine(projectRoot, "src", "bom.cs"),
                [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("class Bom {}\n")]);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mixed.cs"),
                "class Mixed {}\r\nclass Other {}\n");

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (limitExitCode, limitStdout, limitStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--json", "--limit", "1"],
                _jsonOptions));
            var (topExitCode, topStdout, topStderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--json", "--top", "1"],
                _jsonOptions));

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
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
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
    public void RunValidate_JsonArrayEmitsIssueArray_Issue3010()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_validate_json_array");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllBytes(
                Path.Combine(projectRoot, "src", "bom.cs"),
                [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("class Bom {}\n")]);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "clean.cs"),
                "class Clean {}\n");

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--json=array", "--limit", "1"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(JsonValueKind.Array, root.ValueKind);
            Assert.Equal(1, root.GetArrayLength());
            Assert.Equal("bom", root[0].GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunValidate_JsonArrayEmptyEmitsEmptyArray_Issue3010()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_validate_json_array_empty");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "clean.cs"),
                "class Clean {}\n");

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--json=array"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var root = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(JsonValueKind.Array, root.ValueKind);
            Assert.Empty(root.EnumerateArray());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void RunValidate_KindFilterNarrowsIssues()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_validate_kind_filter");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllBytes(
                Path.Combine(projectRoot, "src", "bom.cs"),
                [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes("class Bom {}\n")]);
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "mixed.cs"),
                "class Mixed {}\r\nclass Other {}\n");

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--json", "--kind", "bom"],
                _jsonOptions));

            using var document = ParseJsonOutput(stdout);
            var json = document.RootElement;

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Equal(1, json.GetProperty("count").GetInt32());
            Assert.Equal("bom", json.GetProperty("issues")[0].GetProperty("kind").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    // `validate --kind replacement_chra` previously filtered the file_issues table by an
    // unknown kind, returned zero rows, and printed the same "No encoding issues found."
    // message a genuinely-clean repo would print — silently masking the typo. Round-2 adds
    // a known-kind allowlist + did-you-mean hint (#1582).
    // 従来 `validate --kind replacement_chra` は file_issues を 0 行に絞り込み、本当に
    // クリーンな状態と同じ "No encoding issues found." を出して typo を握り潰していた。
    // round-2 で許可された kind 一覧と did-you-mean を追加した (#1582)。
    [Fact]
    public void RunValidate_KindTypo_SuggestsClosestKind_Issue1582()
    {
        var projectRoot = TestProjectHelper.CreateTempProject("cdidx_validate_kind_typo");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
            File.WriteAllText(
                Path.Combine(projectRoot, "src", "clean.cs"),
                "class Clean {}\n");

            var (indexExitCode, _, indexStderr) = CaptureConsole(() => IndexCommandRunner.Run(
                [projectRoot, "--db", dbPath, "--json", "--quiet"],
                _jsonOptions));
            Assert.Equal(CommandExitCodes.Success, indexExitCode);
            Assert.Equal(string.Empty, indexStderr);

            var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunValidate(
                ["--db", dbPath, "--kind", "replacement_chra"],
                _jsonOptions));

            Assert.Equal(CommandExitCodes.Success, exitCode);
            Assert.Contains("No encoding issues found.", stderr);
            Assert.Contains("'replacement_chra' is not a known validate kind", stderr);
            Assert.Contains("Did you mean: --kind replacement_char?", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }
}
