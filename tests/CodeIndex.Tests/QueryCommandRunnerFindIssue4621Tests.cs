using System.Text.Json;
using CodeIndex.Cli;
using Xunit;

namespace CodeIndex.Tests;

public partial class QueryCommandRunnerTests
{
    [Theory]
    [InlineData(new[] { "--context", "2" }, 2, 2)]
    [InlineData(new[] { "--context=2" }, 2, 2)]
    [InlineData(new[] { "--before", "1", "--context", "3", "--after", "2" }, 1, 2)]
    [InlineData(new[] { "--context", "3", "--after", "2", "--before", "1" }, 1, 2)]
    [InlineData(new[] { "--snippet-lines", "5", "--before", "1" }, 1, 3)]
    [InlineData(new[] { "--snippet-lines", "9", "--context", "2", "--before", "1" }, 1, 2)]
    public void RunFind_ContextIsSymmetricAndExplicitSidesWinRegardlessOfOrder_Issue4621(
        string[] contextArgs,
        int expectedBefore,
        int expectedAfter)
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_context_4621");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/context.cs",
            "csharp",
            "line1\nline2\nline3\nline4\nIssue4621Needle\nline6\nline7\nline8\nline9\n");

        var args = new[]
        {
            "Issue4621Needle", "--db", dbPath, "--path", "src/context.cs", "--json",
        }.Concat(contextArgs).ToArray();
        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(args, _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout.Trim().Split('\n')[0]);
        Assert.Equal(5 - expectedBefore, document.RootElement.GetProperty("start_line").GetInt32());
        Assert.Equal(5 + expectedAfter, document.RootElement.GetProperty("end_line").GetInt32());
    }

    [Fact]
    public void RunBatch_FindAcceptsContextAndPreservesSymmetricWindow_Issue4621()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_batch_find_context_4621");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/context.cs",
            "csharp",
            "line1\nline2\nIssue4621BatchNeedle\nline4\nline5\n");
        var input = "[\"find\",\"Issue4621BatchNeedle\",\"--path\",\"src/context.cs\",\"--context\",\"1\",\"--json\"]\n";

        var (exitCode, stdout, stderr) = CaptureConsoleWithInput(
            input,
            () => QueryCommandRunner.RunBatch(["--db", dbPath], _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout.Trim().Split('\n')[0]);
        Assert.Equal(2, document.RootElement.GetProperty("start_line").GetInt32());
        Assert.Equal(4, document.RootElement.GetProperty("end_line").GetInt32());
    }

    [Fact]
    public void RunFind_OptionShapedLiteralQueryDoesNotEnableContext_Issue4621()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("cdidx_find_literal_context_4621");
        var dbPath = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(
            dbPath,
            "src/context.cs",
            "csharp",
            "line1\n--context=2\nline3\n");

        var (exitCode, stdout, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["--db", dbPath, "--path", "src/context.cs", "--json", "--", "--context=2"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.Success, exitCode);
        Assert.Equal(string.Empty, stderr);
        using var document = JsonDocument.Parse(stdout.Trim().Split('\n')[0]);
        Assert.Equal(2, document.RootElement.GetProperty("start_line").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("end_line").GetInt32());
    }

    [Theory]
    [InlineData("1001")]
    [InlineData("2147483647")]
    public void RunFind_ContextRejectsValuesAboveDocumentedLimit_Issue4621(string value)
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["needle", "--path", "src/**", "--context", value],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--context", stderr, StringComparison.Ordinal);
        Assert.Contains("1000", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("E008", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void RunFind_CompactContextErrorNamesSymmetricFlag_Issue4621()
    {
        var (exitCode, _, stderr) = CaptureConsole(() => QueryCommandRunner.RunFind(
            ["needle", "--path", "src/**", "--format", "compact", "--context", "1"],
            _jsonOptions));

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("--context", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void FindContextAppearsInSharedHelpAndEveryCompletion_Issue4621()
    {
        Assert.Contains(CliFlagSchema.GetCompletionFlagsForCommand("find"), flag => flag.Name == "--context");
        var (printed, stdout, stderr) = ConsoleCapture.Capture(() => ConsoleUi.PrintCommandUsage("find") ? 1 : 0);

        Assert.Equal(1, printed);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("--context <n>", stdout, StringComparison.Ordinal);
        Assert.Contains("explicit --before or --after overrides only that side regardless of option order", stdout, StringComparison.Ordinal);
        Assert.Contains("--context", ConsoleCompletionRenderer.GetCompletionScript("bash"), StringComparison.Ordinal);
        Assert.Contains("--context", ConsoleCompletionRenderer.GetCompletionScript("zsh"), StringComparison.Ordinal);
        Assert.Contains("-l context", ConsoleCompletionRenderer.GetCompletionScript("fish"), StringComparison.Ordinal);
        Assert.Contains("--context", ConsoleCompletionRenderer.GetCompletionScript("powershell"), StringComparison.Ordinal);
    }
}
