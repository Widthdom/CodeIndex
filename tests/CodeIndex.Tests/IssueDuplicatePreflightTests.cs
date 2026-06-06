using System.Text;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public sealed class IssueDuplicatePreflightTests : IDisposable
{
    private readonly string _tempDir;

    public IssueDuplicatePreflightTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cdidx_issue_preflight_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void TryLoad_CapsOpenIssueCount()
    {
        var draftTitle = "[AI Suggestion] security: Last issue should not be read";
        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < IssueDuplicatePreflight.MaxOpenIssueCount; i++)
        {
            if (i > 0)
                builder.Append(',');
            AppendIssue(builder, i + 1, $"Non matching issue {i}", "https://example.com/issues/" + i, ["enhancement"]);
        }

        builder.Append(',');
        AppendIssue(builder, 9999, draftTitle, "https://example.com/issues/9999", ["enhancement"]);
        builder.Append(']');
        var path = WriteOpenIssuesJson(builder.ToString());

        var loaded = IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error);

        Assert.True(loaded, error);
        Assert.Equal(IssueDuplicatePreflight.MaxOpenIssueCount, preflight.OpenIssueCount);
        Assert.Empty(preflight.FindMatches(draftTitle, ["enhancement"]));
    }

    [Fact]
    public void TryLoad_CapsOpenIssueEntriesBeforeValidation()
    {
        var draftTitle = "[AI Suggestion] security: First valid issue should not be read";
        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < IssueDuplicatePreflight.MaxOpenIssueCount; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append("{}");
        }

        builder.Append(',');
        AppendIssue(builder, 9999, draftTitle, "https://example.com/issues/9999", ["enhancement"]);
        builder.Append(']');
        var path = WriteOpenIssuesJson(builder.ToString());

        var loaded = IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error);

        Assert.True(loaded, error);
        Assert.Equal(0, preflight.OpenIssueCount);
        Assert.Empty(preflight.FindMatches(draftTitle, ["enhancement"]));
    }

    [Fact]
    public void TryLoad_CapsScalarsAndLabelsPerIssue()
    {
        var title = new string('A', IssueDuplicatePreflight.MaxOpenIssueTitleLength + 25);
        var url = "https://example.com/issues/" + new string('u', IssueDuplicatePreflight.MaxOpenIssueUrlLength + 25);
        var labels = new List<string> { "enhancement" };
        for (var i = 0; i < IssueDuplicatePreflight.MaxLabelsPerOpenIssue + 8; i++)
            labels.Add($"label-{i}-" + new string('x', IssueDuplicatePreflight.MaxOpenIssueLabelLength + 25));
        var builder = new StringBuilder();
        builder.Append('[');
        AppendIssue(builder, 1234, title, url, labels);
        builder.Append(']');
        var path = WriteOpenIssuesJson(builder.ToString());

        var loaded = IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error);

        Assert.True(loaded, error);
        var match = Assert.Single(preflight.FindMatches(title, ["enhancement"]));
        Assert.Equal(IssueDuplicatePreflight.MaxOpenIssueTitleLength, match.Title.Length);
        Assert.Equal(IssueDuplicatePreflight.MaxOpenIssueUrlLength, match.Url!.Length);
        Assert.True(match.Labels.Count <= IssueDuplicatePreflight.MaxLabelsPerOpenIssue);
        Assert.All(match.Labels, label => Assert.True(label.Length <= IssueDuplicatePreflight.MaxOpenIssueLabelLength));
    }

    private string WriteOpenIssuesJson(string json)
    {
        var path = Path.Combine(_tempDir, "open-issues.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static void AppendIssue(StringBuilder builder, int number, string title, string url, IReadOnlyList<string> labels)
    {
        builder.Append("{\"number\":");
        builder.Append(number);
        builder.Append(",\"title\":");
        builder.Append(JsonSerializer.Serialize(title));
        builder.Append(",\"url\":");
        builder.Append(JsonSerializer.Serialize(url));
        builder.Append(",\"labels\":[");
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append("{\"name\":");
            builder.Append(JsonSerializer.Serialize(labels[i]));
            builder.Append('}');
        }
        builder.Append("]}");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
