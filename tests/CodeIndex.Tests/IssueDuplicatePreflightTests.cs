using System.Text;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class IssueDuplicatePreflightTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EnvironmentVariableScope _env = EnvironmentVariableScope.Capture("CDIDX_GITHUB_TOKEN", "GITHUB_TOKEN");

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

    [Fact]
    public void TryLoad_GitHubSourceFetchesOpenIssuesWithExplicitToken_Issue3449()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "explicit-token");
        _env.Set("GITHUB_TOKEN", "ignored-token");
        var handler = new RecordingOpenIssuesHandler(
            """
            [
              {
                "number": 3449,
                "title": "Issue-draft duplicate preflight should fetch open GitHub issues directly",
                "labels": [{"name": "enhancement"}],
                "html_url": "https://github.example.test/Widthdom/CodeIndex/issues/3449"
              },
              {
                "number": 1,
                "title": "Pull request entry should be ignored",
                "labels": [{"name": "enhancement"}],
                "html_url": "https://github.example.test/Widthdom/CodeIndex/pull/1",
                "pull_request": {}
              }
            ]
            """);
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var loaded = IssueDuplicatePreflight.TryLoad("github", "Widthdom/CodeIndex", out var preflight, out var error);

        Assert.True(loaded, error);
        Assert.True(preflight.Checked);
        Assert.Equal("github:Widthdom/CodeIndex", preflight.Source);
        Assert.Equal(1, preflight.OpenIssueCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/Widthdom/CodeIndex/issues?state=open&per_page=100&page=1", request.Uri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("explicit-token", request.AuthorizationParameter);
        var match = Assert.Single(preflight.FindMatches(
            "Issue-draft duplicate preflight should fetch open GitHub issues directly",
            ["enhancement"]));
        Assert.Equal(3449, match.Number);
    }

    [Fact]
    public void TryLoad_GitHubSourceRequiresRepository_Issue3449()
    {
        var loaded = IssueDuplicatePreflight.TryLoad("github", repository: null, out var preflight, out var error);

        Assert.False(loaded);
        Assert.False(preflight.Checked);
        Assert.Contains("--open-issues github requires --repo", error);
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
        IssueDuplicatePreflight.s_httpClientOverride = null;
        _env.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class RecordingOpenIssuesHandler(string json) : HttpMessageHandler
    {
        internal List<RecordedOpenIssuesRequest> Requests { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => BuildResponse(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(BuildResponse(request));

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            Requests.Add(new RecordedOpenIssuesRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RecordedOpenIssuesRequest(
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter);
}
