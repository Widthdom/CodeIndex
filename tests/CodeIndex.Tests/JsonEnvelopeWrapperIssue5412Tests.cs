using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;
using static CodeIndex.Tests.QueryCommandTestSupport;

namespace CodeIndex.Tests;

public sealed class JsonEnvelopeWrapperIssue5412Tests
{
    [Fact]
    public void Find_CursorErrorsHonorMachineSelectorsAndByteBudgets_Issue5412()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("find_cursor_json_5412");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "rows.txt", "text", "Needle\nEnd\nNeedle\nEnd\nNeedle\nEnd");
        string[][] selectors =
        [
            ["--json"], ["--json=ndjson"], ["--json-envelope"],
            ["--format", "json"], ["--format=json"],
            ["--fields", "path"], ["--fields=path", "--json"],
            ["--json-envelope", "--fields", "path"],
            ["--compact"], ["--format", "compact"], ["--format=compact"],
            ["--max-json-bytes", "8192"],
        ];
        string[][] modes = [[], ["--regex"], ["--regex", "--multiline", "--window-lines", "2"]];
        var staleRequests = new List<(string[] Args, bool Human)>();
        foreach (var mode in modes)
        {
            var query = mode.Contains("--multiline") ? "Needle\\nEnd" : "Needle";
            string[] args = ["find", query, .. mode, "--path", "rows.txt", "--db", db, "--limit", "1"];
            var (exit, stdout, stderr) = Run([.. args, "--json-envelope", "--fields", "path,line"]);
            Assert.Equal(CommandExitCodes.Success, exit);
            Assert.Empty(stderr);
            using var firstPage = JsonDocument.Parse(stdout);
            var cursor = firstPage.RootElement.GetProperty("metadata").GetProperty("next_cursor").GetString()!;
            Assert.StartsWith("response:v2:", cursor, StringComparison.Ordinal);

            foreach (var selector in selectors)
            {
                AssertCursorError([.. args, "--cursor", "bad", .. selector], "cursor_malformed");
                AssertCursorError([.. args, .. selector, "--exact", "--cursor", cursor], "cursor_mismatch");
                staleRequests.Add(([.. args, .. selector, "--cursor", cursor], false));
            }
            AssertHumanError([.. args, "--cursor", "bad"], "cursor_malformed");
            AssertHumanError([.. args, "--format", "text", "--exact", "--cursor", cursor], "cursor_mismatch");
            AssertErrorBudgets([.. args, "--json", "--cursor", "bad"], "cursor_malformed");
            AssertErrorBudgets([.. args, "--json", "--exact", "--cursor", cursor], "cursor_mismatch");
            // Valid replay keeps its normal envelope and advances to a different occurrence.
            var (replayExit, replay, replayError) = Run([.. args, "--json-envelope", "--fields", "path,line", "--cursor", cursor]);
            Assert.Equal(CommandExitCodes.Success, replayExit);
            Assert.Empty(replayError);
            using var page = JsonDocument.Parse(replay);
            Assert.Equal(3, page.RootElement.GetProperty("results")[0].GetProperty("line").GetInt32());
            staleRequests.Add(([.. args, "--cursor", cursor], true));
        }

        TestProjectHelper.InsertIndexedFile(db, "changed.txt", "text", "generation changed");
        foreach (var (args, human) in staleRequests)
        {
            if (human)
                AssertHumanError(args, "cursor_stale");
            else
                AssertCursorError(args, "cursor_stale");
        }
        AssertErrorBudgets(staleRequests[0].Args, "cursor_stale");

        // Output-looking values and the literal query marker do not select machine output.
        foreach (var args in new[]
        {
            new[] { "find", "--path", "rows.txt", "--cursor", "bad", "--query", "--json" },
            new[] { "find", "--path", "rows.txt", "--cursor", "bad", "--", "--json-envelope" },
            new[] { "find", "Needle", "--path", "--compact", "--cursor", "bad" },
        })
            AssertHumanError(args, "cursor_malformed");
        AssertCursorError(["search", "Needle", "--json", "--cursor", "bad"], "cursor_malformed", "search");
    }

    [Fact]
    public void Find_CountCursorAndBoundedControlErrorsRetainTheirContracts_Issue5412()
    {
        using var project = TestProjectHelper.CreateTempProjectScope("find_count_cursor_5412");
        var db = TestProjectHelper.CreateProjectDb(project.Root);
        TestProjectHelper.InsertIndexedFile(db, "rows.txt", "text", "Needle\nNeedle");
        string[] args = ["find", "Needle", "--all", "--count", "--db", db, "--line-scan-limit", "1"];
        var (invalidExit, invalidOutput, invalidError) = Run([.. args, "--json", "--cursor", "bad"]);
        Assert.Equal(CommandExitCodes.UsageError, invalidExit);
        Assert.Empty(invalidError);
        using var invalidDocument = JsonDocument.Parse(invalidOutput);
        Assert.Equal("cursor_malformed", invalidDocument.RootElement.GetProperty("category").GetString());
        Assert.Equal(CommandErrorCodes.UsageError, invalidDocument.RootElement.GetProperty("error_code").GetString());
        AssertHumanError([.. args, "--cursor", "bad"], "find count cursor must be an opaque resumable response:v2 cursor");
        var (exit, stdout, stderr) = Run([.. args, "--json"]);
        Assert.Equal(CommandExitCodes.PartialResult, exit);
        Assert.Empty(stderr);
        using var first = JsonDocument.Parse(stdout);
        var cursor = first.RootElement.GetProperty("next_cursor").GetString()!;
        var (resumedExit, resumed, resumedError) = Run([.. args, "--json", "--cursor", cursor]);
        Assert.Equal(CommandExitCodes.Success, resumedExit);
        Assert.Empty(resumedError);
        using var last = JsonDocument.Parse(resumed);
        Assert.Equal(1, last.RootElement.GetProperty("count").GetInt32());
        Assert.False(last.RootElement.TryGetProperty("metadata", out _));

        foreach (var control in new[] { new[] { "--limit", "0" }, new[] { "--count" } })
        {
            string[] invalid = ["find", "Needle", "--path", "rows.txt", "--fields", "path", .. control];
            var error = AssertCursorError(invalid, "usage");
            Assert.Contains(control[0], error.GetProperty("message").GetString(), StringComparison.Ordinal);
            AssertErrorBudgets(invalid, "usage");
        }
    }

    private static void AssertErrorBudgets(string[] args, string category)
    {
        foreach (var pretty in new[] { Array.Empty<string>(), new[] { "--pretty" } })
        {
            var request = args.Concat(pretty).ToArray();
            var (exit, stdout, stderr) = Run(request);
            Assert.Equal(CommandExitCodes.UsageError, exit);
            Assert.Empty(stderr);
            var bytes = Encoding.UTF8.GetByteCount(stdout);
            var exact = AssertCursorError([.. request, "--max-json-bytes", bytes.ToString(CultureInfo.InvariantCulture)], category);
            using var original = JsonDocument.Parse(stdout);
            Assert.Equal(original.RootElement.GetRawText(), exact.GetRawText());
            foreach (var budget in new[] { 1, bytes - 1 })
            {
                // The cap must be found even before control parsing reaches a failure.
                var (budgetExit, budgetOut, budgetError) = Run([request[0], "--max-json-bytes=" + budget.ToString(CultureInfo.InvariantCulture), .. request[1..]]);
                Assert.Equal(CommandExitCodes.UsageError, budgetExit);
                Assert.Empty(budgetError);
                using var document = JsonDocument.Parse(budgetOut);
                var error = document.RootElement;
                Assert.Equal(CommandErrorCodes.ResponseBudgetTooSmall, error.GetProperty("error_code").GetString());
                Assert.Equal(budget, error.GetProperty("requested_bytes").GetInt32());
                Assert.Equal(budget, error.GetProperty("effective_bytes").GetInt32());
                Assert.True(error.GetProperty("minimum_required_bytes_known").GetBoolean());
                Assert.False(error.GetProperty("minimum_required_bytes_uncertain").GetBoolean());
                Assert.Equal(bytes, error.GetProperty("minimum_required_bytes").GetInt32());
                Assert.Equal(bytes, error.GetProperty("retry").GetProperty("recommended_bytes").GetInt32());
                Assert.Equal(category, error.GetProperty("validation_error").GetProperty("category").GetString());
                Assert.Equal(JsonSerializer.Serialize(original.RootElement), JsonSerializer.Serialize(error.GetProperty("validation_error")));
            }
        }
    }

    private static JsonElement AssertCursorError(string[] args, string category, string command = "find")
    {
        var (exit, stdout, stderr) = Run(args);
        Assert.Equal(CommandExitCodes.UsageError, exit);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement;
        Assert.Equal("1", error.GetProperty("api_version").GetString());
        Assert.Equal("error", error.GetProperty("status").GetString());
        Assert.Equal(CommandErrorCodes.UsageError, error.GetProperty("error_code").GetString());
        Assert.Equal(CommandExitCodes.UsageError, error.GetProperty("exit_code").GetInt32());
        Assert.Equal(command, error.GetProperty("command").GetString());
        Assert.Equal(category, error.GetProperty("category").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("hint").GetString()));
        Assert.False(error.TryGetProperty("next_cursor", out _));
        if (category != "usage")
            Assert.Contains(category, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        return error.Clone();
    }

    private static void AssertHumanError(string[] args, string reason)
    {
        var (exit, stdout, stderr) = Run(args);
        Assert.Equal(CommandExitCodes.UsageError, exit);
        Assert.Empty(stdout);
        Assert.Contains(CommandErrorCodes.UsageError, stderr, StringComparison.Ordinal);
        Assert.Contains(reason, stderr, StringComparison.Ordinal);
        Assert.Contains("Hint:", stderr, StringComparison.Ordinal);
    }

    private static (int Result, string Stdout, string Stderr) Run(string[] args)
        => CaptureConsole(() => ProgramRunner.Run(args, JsonOptions, "test"));
}
