using System.Net;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class IssueDuplicatePreflightTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EnvironmentVariableScope _env = EnvironmentVariableScope.Capture(
        "CDIDX_GITHUB_TOKEN",
        "GITHUB_TOKEN",
        GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable);

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
    public void TryLoad_OversizedIssueNumberScalar_ReturnsInvalidPreflightFile_Issue3466()
    {
        var issueNumber = new string('9', IssueDuplicatePreflight.MaxOpenIssueNumberLength + 1);
        var path = WriteOpenIssuesJson(
            $$"""
            [
              {
                "number": "{{issueNumber}}",
                "title": "Oversized number should fail",
                "labels": [{"name": "bug"}],
                "url": "https://example.com/issues/1"
              }
            ]
            """);

        var loaded = IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error);

        Assert.False(loaded);
        Assert.False(preflight.Checked);
        Assert.Contains("invalid-preflight-file", error);
        Assert.Contains(IssueDuplicatePreflight.MaxOpenIssueNumberLength.ToString(System.Globalization.CultureInfo.InvariantCulture), error);
    }

    [Fact]
    public void TryLoad_ReadFailureDiagnosticUsesSanitizedPathAndException_Issue3778()
    {
        var secretDirectory = Path.Combine(_tempDir, "secret-workspace-token");
        Directory.CreateDirectory(secretDirectory);
        var path = Path.Combine(secretDirectory, "missing-open-issues.json");

        var loaded = IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error);

        Assert.False(loaded);
        Assert.False(preflight.Checked);
        Assert.Contains("missing-open-issues.json", error);
        Assert.Contains(nameof(FileNotFoundException), error);
        Assert.DoesNotContain(secretDirectory, error);
        Assert.DoesNotContain(_tempDir, error);
    }

    [Fact]
    public void TryLoad_ReadFailureDiagnosticSanitizesWindowsStylePath_Issue3778()
    {
        var path = @"C:\Users\secret-workspace-token\missing-open-issues.json";

        var loaded = IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error);

        Assert.False(loaded);
        Assert.False(preflight.Checked);
        Assert.Contains("missing-open-issues.json", error);
        Assert.DoesNotContain("secret-workspace-token", error);
        Assert.DoesNotContain(@"C:\Users", error);
    }

    [Fact]
    public void TryLoad_InvalidFileDiagnosticUsesSanitizedPathAndBoundedDetail_Issue3778()
    {
        var secretDirectory = Path.Combine(_tempDir, "secret-invalid-json-parent");
        Directory.CreateDirectory(secretDirectory);
        var path = Path.Combine(secretDirectory, "open-issues.json");
        var issueNumber = new string('9', IssueDuplicatePreflight.MaxOpenIssueNumberLength + 1);
        File.WriteAllText(
            path,
            $$"""
            [
              {
                "number": "{{issueNumber}}",
                "title": "Oversized number should fail",
                "labels": [{"name": "bug"}],
                "url": "https://example.com/issues/1"
              }
            ]
            """);

        var loaded = IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error);

        Assert.False(loaded);
        Assert.False(preflight.Checked);
        Assert.Contains("open-issues.json", error);
        Assert.Contains("invalid-preflight-file", error);
        Assert.Contains(IssueDuplicatePreflight.MaxOpenIssueNumberLength.ToString(System.Globalization.CultureInfo.InvariantCulture), error);
        Assert.DoesNotContain(secretDirectory, error);
        Assert.DoesNotContain(_tempDir, error);
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
                "url": "https://api.github.example.test/repos/Widthdom/CodeIndex/issues/3449",
                "html_url": "https://github.example.test/Widthdom/CodeIndex/issues/3449"
              },
              {
                "number": 1,
                "title": "Pull request entry should be ignored",
                "labels": [{"name": "enhancement"}],
                "url": "https://api.github.example.test/repos/Widthdom/CodeIndex/issues/1",
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
        Assert.True(preflight.RepositoryLabelsChecked);
        Assert.Empty(preflight.RepositoryLabels);
        Assert.Equal(2, handler.Requests.Count);
        var request = handler.Requests[0];
        Assert.Equal("https://api.github.com/repos/Widthdom/CodeIndex/issues?state=open&per_page=100&page=1", request.Uri);
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("explicit-token", request.AuthorizationParameter);
        var labelsRequest = handler.Requests[1];
        Assert.Equal("https://api.github.com/repos/Widthdom/CodeIndex/labels?per_page=100&page=1", labelsRequest.Uri);
        Assert.Equal("Bearer", labelsRequest.AuthorizationScheme);
        Assert.Equal("explicit-token", labelsRequest.AuthorizationParameter);
        var match = Assert.Single(preflight.FindMatches(
            "Issue-draft duplicate preflight should fetch open GitHub issues directly",
            ["enhancement"]));
        Assert.Equal(3449, match.Number);
        Assert.Equal("https://github.example.test/Widthdom/CodeIndex/issues/3449", match.Url);
    }

    [Fact]
    public void TryLoad_GitHubSourceFetchesRepositoryLabels_Issue3926()
    {
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(new SingleResponseHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/labels", StringComparison.Ordinal)
                ? """
                  [
                    {"name":"bug"},
                    {"name":"Security"},
                    {"name":"bug"}
                  ]
                  """
                : "[]";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }));

        var loaded = IssueDuplicatePreflight.TryLoad("github", "Widthdom/CodeIndex", out var preflight, out var error);

        Assert.True(loaded, error);
        Assert.True(preflight.Checked);
        Assert.True(preflight.RepositoryLabelsChecked);
        Assert.Equal(["bug", "Security"], preflight.RepositoryLabels);
    }

    [Fact]
    public void TryLoad_GitHubSourceRequiresRepository_Issue3449()
    {
        var loaded = IssueDuplicatePreflight.TryLoad("github", repository: null, out var preflight, out var error);

        Assert.False(loaded);
        Assert.False(preflight.Checked);
        Assert.Contains("--open-issues github requires --repo", error);
    }

    [Fact]
    public async Task TryLoadAsync_GitHubSourcePropagatesCallerCancellation_Issue3823()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex", cts.Token));
    }

    [Fact]
    public async Task TryLoadAsync_GitHubRateLimitIncludesRetryMetadata_Issue3823()
    {
        var fixedNow = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);
        var previousTimeProvider = GitHubIssueReporter.TimeProvider;
        GitHubIssueReporter.TimeProvider = new ManualTimeProvider(fixedNow);
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(new SingleResponseHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("""{"message":"rate limited","token":"secret-value"}""", Encoding.UTF8, "application/json"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return response;
        }));
        try
        {
            var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

            Assert.False(result.Loaded);
            Assert.Contains("429", result.Error);
            Assert.Contains("next_retry_at=", result.Error);
            Assert.Contains(fixedNow.UtcDateTime.AddSeconds(45).ToString("O", System.Globalization.CultureInfo.InvariantCulture), result.Error);
            Assert.DoesNotContain("secret-value", result.Error);
        }
        finally
        {
            GitHubIssueReporter.TimeProvider = previousTimeProvider;
        }
    }

    [Fact]
    public async Task TryLoadAsync_GitHubFetchExceptionIsSanitized_Issue3823()
    {
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(new ThrowingOpenIssuesHandler(
            new HttpRequestException("secret host detail")));

        var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

        Assert.False(result.Loaded);
        Assert.Contains("HttpRequestException", result.Error);
        Assert.DoesNotContain("secret host detail", result.Error);
    }

    [Fact]
    public async Task TryLoadAsync_GitHubInvalidUtf8ExpansionIsRecoverable_Issue4127()
    {
        var responseBytes = Enumerable
            .Repeat((byte)0x80, (IssueDuplicatePreflight.MaxOpenIssuesJsonBytes / 3) + 1)
            .ToArray();
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(new SingleResponseHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes),
            }));

        var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

        Assert.False(result.Loaded);
        Assert.False(result.Preflight.Checked);
        Assert.Contains(nameof(InvalidDataException), result.Error);
        Assert.Contains("could not fetch --open-issues github", result.Error);
    }

    [Fact]
    public async Task TryLoadAsync_GitHubInternalCancellationIsSanitized_Issue3823()
    {
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(new ThrowingOpenIssuesHandler(
            new OperationCanceledException("secret timeout detail")));

        var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

        Assert.False(result.Loaded);
        Assert.Contains("OperationCanceledException", result.Error);
        Assert.DoesNotContain("secret timeout detail", result.Error);
    }

    [Fact]
    public void FindMatches_UsesEvidenceAndBodySignals_Issue3823()
    {
        var path = WriteOpenIssuesJson(
            """
            [
              {
                "number": 3823,
                "title": "Different issue title",
                "labels": [{"name": "enhancement"}],
                "url": "https://example.test/issues/3823",
                "body": "## Evidence paths\n- src/CodeIndex/Cli/IssueDuplicatePreflight.cs\n\nretry diagnostics cancellation duplicate preflight github"
              }
            ]
            """);

        var loaded = IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error);

        Assert.True(loaded, error);
        var match = Assert.Single(preflight.FindMatches(
            "Unrelated title",
            ["enhancement"],
            ["src/CodeIndex/Cli/IssueDuplicatePreflight.cs"],
            "retry diagnostics cancellation duplicate preflight github"));
        Assert.Equal("evidence_path_overlap", match.Reason);
        Assert.Equal("high", match.Confidence);
        Assert.Contains("evidence_path_overlap", match.Signals);
        Assert.Contains("body_similarity", match.Signals);
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
        TestProjectHelper.DeleteDirectory(_tempDir);
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

    private sealed class SingleResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }

    private sealed class ThrowingOpenIssuesHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed record RecordedOpenIssuesRequest(
        string Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter);
}
