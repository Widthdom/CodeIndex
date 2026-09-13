using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class SuggestionLinkCommandTests : IDisposable
{
    private readonly string _root = TestProjectHelper.CreateTempProject("suggestion_link");
    private string DbPath => Path.Combine(_root, "codeindex.db");

    [Fact]
    public void Link_ExposesAssociationAcrossReadAndExportSurfaces_Issue5350()
    {
        var store = new SuggestionStore(_root);
        foreach (var (category, description) in new[] { ("bug", "First manual finding"), ("performance", "Second consolidated observation") })
            Assert.True(store.TryAdd(new SuggestionRecord { Category = category, Description = description }));
        var records = store.LoadAll();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var issue = index == 0 ? "5350" : "https://github.com/Widthdom/CodeIndex/issues/5350";
            using var linked = RunJson("link", record.Id[..12], "--repo=Widthdom/CodeIndex", "--issue", issue, "--actor", "maintainer", "--reason", "Published with GitHub CLI");
            Assert.Equal("linked", linked.RootElement.GetProperty("action").GetString());
            AssertLinked(linked.RootElement.GetProperty("suggestion"));
            using var shown = RunJson("show", record.Id);
            AssertLinked(shown.RootElement);
        }
        using var listed = RunJson("list", "--status", "submitted");
        Assert.Equal(2, listed.RootElement.GetProperty("total_count").GetInt32());
        foreach (var item in listed.RootElement.GetProperty("results").EnumerateArray())
            AssertLinked(item);
        using var unsubmitted = RunJson("list", "--status", "unsubmitted");
        Assert.Equal(0, unsubmitted.RootElement.GetProperty("total_count").GetInt32());
        using var exported = RunJson("export", "--format", "json");
        foreach (var item in exported.RootElement.GetProperty("suggestions").EnumerateArray())
            AssertLinked(item);
        using var drafts = RunJson("export", "--format", "issue-drafts");
        Assert.False(drafts.RootElement.GetProperty("duplicate_preflight").GetProperty("checked").GetBoolean());
        foreach (var draft in drafts.RootElement.GetProperty("drafts").EnumerateArray())
        {
            var preflight = draft.GetProperty("duplicate_preflight");
            Assert.True(preflight.GetProperty("already_published").GetBoolean());
            Assert.Equal(5350, preflight.GetProperty("upstream_issue_number").GetInt32());
            Assert.Contains("do not file again", draft.GetProperty("triage").GetProperty("duplicate_guidance").GetString());
            Assert.Equal("manual_external", draft.GetProperty("source").GetProperty("upstream_association").GetProperty("provenance").GetString());
        }
        foreach (var args in new string[][] { ["show", records[0].Id], ["export", "--format", "markdown"] })
        {
            var (exit, stdout, stderr) = Run(args);
            Assert.Equal(CommandExitCodes.Success, exit);
            Assert.Empty(stderr);
            Assert.Contains("manual_external", stdout);
            Assert.Contains("not_performed", stdout);
            Assert.Contains("https://github.com/widthdom/codeindex/issues/5350", stdout);
        }
        using var conflict = RunJsonFailure("link", records[0].Id, "--repo", "Widthdom/CodeIndex", "--issue", "5351");
        Assert.Equal("upstream_association_conflict", conflict.RootElement.GetProperty("category").GetString());
        using var update = RunJsonFailure("update", records[0].Id, "--description", "Do not rewrite published history");
        using var delete = RunJsonFailure("delete", records[0].Id);
        Assert.Equal(2, store.LoadAll().Count);
    }

    [Fact]
    public void Link_RejectsMissingIdentityAndUnrelatedFlags_Issue5350()
    {
        foreach (var args in new string[][]
        {
            ["link"],
            ["link", "id", "--repo", "owner/repo"],
            ["link", "id", "--issue", "1"],
            ["link", "id", "--repo", "owner/repo", "--issue", "https://github.com/wrong/repo/issues/1"],
            ["link", "id", "--repo", "owner/repo", "--issue", "1", "--actor", ""],
            ["link", "id", "--repo", "owner/repo", "--issue", "1", "--status", "resolved_in_upstream"],
            ["link", "id", "--repo", "owner/repo", "--issue", "1", "--description", "edit"],
            ["link", "id", "--repo", "owner/repo", "--issue", "1", "--open-issues", "github"],
            ["link", "id", "--repo", "owner/repo", "--issue", "1", "--limit", "1"],
            ["list", "--issue", "1"],
            ["export", "--format", "issue-drafts", "--issue", "1"],
        })
        {
            using var error = RunJsonFailure(args);
            Assert.Equal("error", error.RootElement.GetProperty("status").GetString());
        }
        Assert.False(File.Exists(new SuggestionStore(_root).FilePath));
    }

    private static void AssertLinked(JsonElement record)
    {
        Assert.Equal("submitted_pending_triage", record.GetProperty("status").GetString());
        Assert.True(record.GetProperty("submitted_to_github").GetBoolean());
        Assert.Equal(0, record.GetProperty("submit_attempt_count").GetInt32());
        Assert.False(record.TryGetProperty("last_submit_attempt", out _));
        Assert.Equal(5350, record.GetProperty("upstream_issue_number").GetInt32());
        var association = record.GetProperty("upstream_association");
        Assert.Equal("widthdom/codeindex", association.GetProperty("repository").GetString());
        Assert.Equal("manual_external", association.GetProperty("provenance").GetString());
        Assert.Equal("not_performed", association.GetProperty("verification").GetString());
        Assert.Equal("maintainer", association.GetProperty("linked_by").GetString());
    }

    private JsonDocument RunJson(params string[] args) => RunJsonExpected(CommandExitCodes.Success, args);
    private JsonDocument RunJsonFailure(params string[] args) => RunJsonExpected(CommandExitCodes.UsageError, args);

    private JsonDocument RunJsonExpected(int expectedExit, string[] args)
    {
        var (exit, stdout, stderr) = Run([.. args, "--json"]);
        Assert.Equal(expectedExit, exit);
        Assert.Empty(stderr);
        return JsonDocument.Parse(stdout);
    }

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
        => ConsoleCapture.Capture(() => SuggestionsCommandRunner.Run([.. args, "--db", DbPath], ProgramRunner.CreateDefaultJsonOptions()));

    public void Dispose() => TestProjectHelper.DeleteDirectory(_root);
}
