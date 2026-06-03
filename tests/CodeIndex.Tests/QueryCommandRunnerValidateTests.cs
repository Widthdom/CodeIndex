using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
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
