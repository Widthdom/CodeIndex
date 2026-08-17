using System.Net;
using System.Text;
using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class IssueDuplicatePreflightTests : IDisposable
{
    [Fact]
    public void FindMatches_ClosedIssueClassifiesPossibleRegression_Issue4430()
    {
        var path = WriteOpenIssuesJson("""[{"number":4430,"title":"Duplicate history","state":"closed","labels":["bug"]}]""");
        Assert.True(IssueDuplicatePreflight.TryLoad(path, out var preflight, out var error), error);

        var match = Assert.Single(preflight.FindMatches("Duplicate history", ["bug"]));
        Assert.Equal("closed", match.State);
        Assert.Equal("possible_regression", match.Classification);
    }

    private readonly string _tempDir;
    private readonly EnvironmentVariableScope _env = EnvironmentVariableScope.Capture(
        "CDIDX_GITHUB_TOKEN",
        "GITHUB_TOKEN",
        GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable);

    public IssueDuplicatePreflightTests()
    {
        _tempDir = TestProjectHelper.CreateTempProject("cdidx_issue_preflight");
        _env.Set("CDIDX_GITHUB_TOKEN", "test-token");
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
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(
            HttpStatusCode.OK,
            BuildGraphQlIssuePage(
                hasNextPage: false,
                endCursor: null,
                new GraphQlIssueFixture(
                    3449,
                    "Issue-draft duplicate preflight should fetch open GitHub issues directly",
                    "issue-node-3449",
                    Labels: ["enhancement"])));
        handler.QueueJson(HttpStatusCode.OK, "[]");
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var loaded = IssueDuplicatePreflight.TryLoad("github", "Widthdom/CodeIndex", out var preflight, out var error);

        Assert.True(loaded, error);
        Assert.True(preflight.Checked);
        Assert.Equal("github:Widthdom/CodeIndex", preflight.Source);
        Assert.Equal(1, preflight.OpenIssueCount);
        Assert.True(preflight.RepositoryLabelsChecked);
        Assert.Empty(preflight.RepositoryLabels);
        Assert.Equal(2, handler.RequestCount);
        var request = handler.Requests[0];
        var variables = ReadGraphQlVariables(request);
        Assert.Equal("Widthdom", variables.GetProperty("owner").GetString());
        Assert.Equal("CodeIndex", variables.GetProperty("name").GetString());
        Assert.Equal(100, variables.GetProperty("first").GetInt32());
        Assert.Equal(JsonValueKind.Null, variables.GetProperty("after").ValueKind);
        Assert.Equal(["OPEN"], ReadGraphQlStates(variables));
        Assert.Equal("Bearer", request.AuthorizationScheme);
        Assert.Equal("explicit-token", request.AuthorizationParameter);
        var labelsRequest = handler.Requests[1];
        Assert.Equal(HttpMethod.Get, labelsRequest.Method);
        Assert.Equal("https://api.github.com/repos/Widthdom/CodeIndex/labels?per_page=100&page=1", labelsRequest.RequestUri?.ToString());
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
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(HttpStatusCode.OK, BuildGraphQlIssuePage(hasNextPage: false, endCursor: null));
        handler.QueueJson(
            HttpStatusCode.OK,
            """
            [
              {"name":"bug"},
              {"name":"Security"},
              {"name":"bug"}
            ]
            """);
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var loaded = IssueDuplicatePreflight.TryLoad("github", "Widthdom/CodeIndex", out var preflight, out var error);

        Assert.True(loaded, error);
        Assert.True(preflight.Checked);
        Assert.True(preflight.RepositoryLabelsChecked);
        Assert.Equal(["bug", "Security"], preflight.RepositoryLabels);
    }

    [Fact]
    public async Task TryLoadAsync_GitHubGraphQlTraversesThreeCursorPagesAndDeduplicatesExactlyOnce_Issue5090()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(
            HttpStatusCode.OK,
            BuildGraphQlIssuePage(
                hasNextPage: true,
                endCursor: "cursor-1",
                new GraphQlIssueFixture(1001, "Cursor first alpha", "issue-node-1001", Labels: ["bug"]),
                new GraphQlIssueFixture(1002, "Canonical duplicate beta", "issue-node-1002", Labels: ["bug"])));
        handler.QueueJson(
            HttpStatusCode.OK,
            BuildGraphQlIssuePage(
                hasNextPage: true,
                endCursor: "cursor-2",
                new GraphQlIssueFixture(1002, "Replacement duplicate omega", "issue-node-1002", Labels: ["bug"]),
                new GraphQlIssueFixture(1003, "Cursor second gamma", "issue-node-1003", Labels: ["bug"])));
        handler.QueueJson(
            HttpStatusCode.OK,
            BuildGraphQlIssuePage(
                hasNextPage: false,
                endCursor: null,
                new GraphQlIssueFixture(1004, "Cursor third delta", "issue-node-1004", Labels: ["bug"])));
        handler.QueueJson(HttpStatusCode.OK, "[]");
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync(
            "github",
            "Widthdom/CodeIndex",
            issueState: "all",
            maximumIssueCount: 201);

        Assert.True(result.Loaded, result.Error);
        Assert.True(result.Preflight.Checked);
        Assert.Equal(4, result.Preflight.OpenIssueCount);
        var canonical = Assert.Single(result.Preflight.FindMatches("Canonical duplicate beta", ["bug"])
            .Where(match => match.Number == 1002));
        Assert.Equal("Canonical duplicate beta", canonical.Title);
        Assert.DoesNotContain(
            result.Preflight.FindMatches("Replacement duplicate omega", ["bug"]),
            match => match.Number == 1002);
        Assert.Equal(4, handler.RequestCount);

        var expectedCursors = new string?[] { null, "cursor-1", "cursor-2" };
        for (var i = 0; i < expectedCursors.Length; i++)
        {
            var variables = ReadGraphQlVariables(handler.Requests[i]);
            Assert.Equal(100, variables.GetProperty("first").GetInt32());
            Assert.Equal(["OPEN", "CLOSED"], ReadGraphQlStates(variables));
            if (expectedCursors[i] is null)
                Assert.Equal(JsonValueKind.Null, variables.GetProperty("after").ValueKind);
            else
                Assert.Equal(expectedCursors[i], variables.GetProperty("after").GetString());
        }

        Assert.Equal(HttpMethod.Get, handler.Requests[3].Method);
        Assert.Equal(
            "https://api.github.com/repos/Widthdom/CodeIndex/labels?per_page=100&page=1",
            handler.Requests[3].RequestUri?.ToString());
    }

    [Theory]
    [InlineData("open", "OPEN")]
    [InlineData("closed", "CLOSED")]
    [InlineData("all", "OPEN,CLOSED")]
    public async Task TryLoadAsync_GitHubGraphQlPassesRequestedIssueStates_Issue5090(
        string issueState,
        string expectedStates)
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(HttpStatusCode.OK, BuildGraphQlIssuePage(hasNextPage: false, endCursor: null));
        handler.QueueJson(HttpStatusCode.OK, "[]");
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync(
            "github",
            "Widthdom/CodeIndex",
            issueState: issueState);

        Assert.True(result.Loaded, result.Error);
        var variables = ReadGraphQlVariables(handler.Requests[0]);
        Assert.Equal(expectedStates.Split(','), ReadGraphQlStates(variables));
    }

    [Fact]
    public async Task TryLoadAsync_GitHubGraphQlHonorsMaximumIssueBoundAcrossPages_Issue5090()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(
            HttpStatusCode.OK,
            BuildGraphQlIssuePage(
                hasNextPage: true,
                endCursor: "cursor-bound-1",
                new GraphQlIssueFixture(2001, "Bound first alpha", "issue-node-2001"),
                new GraphQlIssueFixture(2002, "Bound second beta", "issue-node-2002")));
        handler.QueueJson(
            HttpStatusCode.OK,
            BuildGraphQlIssuePage(
                hasNextPage: true,
                endCursor: "cursor-bound-2",
                new GraphQlIssueFixture(2003, "Bound third gamma", "issue-node-2003")));
        handler.QueueJson(HttpStatusCode.OK, "[]");
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync(
            "github",
            "Widthdom/CodeIndex",
            maximumIssueCount: 3);

        Assert.True(result.Loaded, result.Error);
        Assert.Equal(3, result.Preflight.OpenIssueCount);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(3, ReadGraphQlVariables(handler.Requests[0]).GetProperty("first").GetInt32());
        var secondPageVariables = ReadGraphQlVariables(handler.Requests[1]);
        Assert.Equal(1, secondPageVariables.GetProperty("first").GetInt32());
        Assert.Equal("cursor-bound-1", secondPageVariables.GetProperty("after").GetString());
        Assert.Equal(HttpMethod.Get, handler.Requests[2].Method);
    }

    [Theory]
    [InlineData(false, true, "repeated cursor", 2)]
    [InlineData(false, false, "repeated cursor", 2)]
    [InlineData(true, true, "without an end cursor", 1)]
    public async Task TryLoadAsync_GitHubGraphQlRejectsMissingOrRepeatedCursor_Issue5090(
        bool omitFirstCursor,
        bool repeatedPageHasNextPage,
        string expectedDetail,
        int expectedRequestCount)
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(
            HttpStatusCode.OK,
            BuildGraphQlIssuePage(
                hasNextPage: true,
                endCursor: omitFirstCursor ? null : "cursor-repeat",
                new GraphQlIssueFixture(3001, "Cursor validation alpha", "issue-node-3001")));
        if (!omitFirstCursor)
        {
            handler.QueueJson(
                HttpStatusCode.OK,
                BuildGraphQlIssuePage(
                    hasNextPage: repeatedPageHasNextPage,
                    endCursor: "cursor-repeat",
                    new GraphQlIssueFixture(3002, "Cursor validation beta", "issue-node-3002")));
        }
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync(
            "github",
            "Widthdom/CodeIndex",
            maximumIssueCount: 201);

        Assert.False(result.Loaded);
        Assert.False(result.Preflight.Checked);
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(IssueDuplicatePreflight.GitHubPaginationFailureCategory, result.ErrorCategory);
        Assert.Contains(expectedDetail, result.Error);
        Assert.Equal(expectedRequestCount, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, IssueDuplicatePreflight.GitHubAuthenticationFailureCategory, 1)]
    [InlineData(HttpStatusCode.Forbidden, IssueDuplicatePreflight.GitHubPermissionFailureCategory, 1)]
    [InlineData(HttpStatusCode.TooManyRequests, IssueDuplicatePreflight.GitHubRateLimitFailureCategory, 1)]
    [InlineData(HttpStatusCode.UnprocessableEntity, IssueDuplicatePreflight.GitHubValidationFailureCategory, 1)]
    [InlineData(HttpStatusCode.RequestTimeout, IssueDuplicatePreflight.GitHubTimeoutFailureCategory, 3)]
    [InlineData(HttpStatusCode.InternalServerError, IssueDuplicatePreflight.GitHubTransientFailureCategory, 3)]
    [InlineData(HttpStatusCode.BadRequest, IssueDuplicatePreflight.GitHubResponseFailureCategory, 1)]
    public async Task TryLoadAsync_GitHubGraphQlClassifiesHttpFailures_Issue5090(
        HttpStatusCode statusCode,
        string expectedCategory,
        int expectedRequestCount)
    {
        var handler = new FakeHttpMessageHandler();
        for (var i = 0; i < expectedRequestCount; i++)
        {
            handler.QueueJson(
                statusCode,
                """{"message":"classified failure","token":"secret-http-status"}""");
        }
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

        Assert.False(result.Loaded);
        Assert.False(result.Preflight.Checked);
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(expectedCategory, result.ErrorCategory);
        Assert.Contains(((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), result.Error);
        Assert.DoesNotContain("secret-http-status", result.Error);
        Assert.Equal(expectedRequestCount, handler.RequestCount);
    }

    [Theory]
    [InlineData("UNAUTHENTICATED", IssueDuplicatePreflight.GitHubAuthenticationFailureCategory)]
    [InlineData("NOT_FOUND", IssueDuplicatePreflight.GitHubPermissionFailureCategory)]
    [InlineData("RATE_LIMITED", IssueDuplicatePreflight.GitHubRateLimitFailureCategory)]
    [InlineData("BAD_USER_INPUT", IssueDuplicatePreflight.GitHubValidationFailureCategory)]
    [InlineData("TIMEOUT", IssueDuplicatePreflight.GitHubTimeoutFailureCategory)]
    [InlineData("INTERNAL", IssueDuplicatePreflight.GitHubTransientFailureCategory)]
    [InlineData("UNKNOWN", IssueDuplicatePreflight.GitHubResponseFailureCategory)]
    public async Task TryLoadAsync_GitHubGraphQlClassifiesTopLevelErrors_Issue5090(
        string errorType,
        string expectedCategory)
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new { type = errorType, message = "secret-graphql-error" },
                },
            }));
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

        Assert.False(result.Loaded);
        Assert.False(result.Preflight.Checked);
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(expectedCategory, result.ErrorCategory);
        Assert.DoesNotContain("secret-graphql-error", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TryLoadAsync_GitHubGraphQlRejectsMalformedErrorsField_Issue5090()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(
            HttpStatusCode.OK,
            """
            {
              "errors": { "type": "BAD_USER_INPUT" },
              "data": {
                "repository": {
                  "issues": {
                    "nodes": [],
                    "pageInfo": { "hasNextPage": false, "endCursor": null }
                  }
                }
              }
            }
            """);
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

        Assert.False(result.Loaded);
        Assert.False(result.Preflight.Checked);
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(IssueDuplicatePreflight.GitHubResponseFailureCategory, result.ErrorCategory);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task TryLoadAsync_GitHubGraphQlClassifiesResponseBodyIoFailureAsTransport_Issue5090()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingReadStream(new IOException("secret-body-read"))),
        });
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

        Assert.False(result.Loaded);
        Assert.False(result.Preflight.Checked);
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(IssueDuplicatePreflight.GitHubTransportFailureCategory, result.ErrorCategory);
        Assert.DoesNotContain("secret-body-read", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
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
    public async Task TryLoadAsync_GitHubGraphQlRequiresExplicitToken_Issue5090()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", null);
        _env.Set("GITHUB_TOKEN", "must-not-be-used");
        var handler = new FakeHttpMessageHandler();
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);

        var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

        Assert.False(result.Loaded);
        Assert.False(result.Preflight.Checked);
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(IssueDuplicatePreflight.GitHubAuthenticationFailureCategory, result.ErrorCategory);
        Assert.Contains("CDIDX_GITHUB_TOKEN is required", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task TryLoadAsync_GitHubSecondaryRateLimitRetryAfterIsCategorized_Issue5090()
    {
        var fixedNow = new DateTimeOffset(2026, 8, 18, 1, 2, 3, TimeSpan.Zero);
        var previousTimeProvider = GitHubIssueReporter.TimeProvider;
        GitHubIssueReporter.TimeProvider = new ManualTimeProvider(fixedNow);
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var handler = new FakeHttpMessageHandler();
        handler.QueueResponse(response);
        IssueDuplicatePreflight.s_httpClientOverride = new HttpClient(handler);
        try
        {
            var result = await IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex");

            Assert.False(result.Loaded);
            Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
            Assert.Equal(IssueDuplicatePreflight.GitHubRateLimitFailureCategory, result.ErrorCategory);
            Assert.Contains("next_retry_at=", result.Error, StringComparison.Ordinal);
            Assert.Contains(fixedNow.AddSeconds(30).UtcDateTime.ToString("O"), result.Error, StringComparison.Ordinal);
        }
        finally
        {
            GitHubIssueReporter.TimeProvider = previousTimeProvider;
        }
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
            Assert.False(result.Preflight.Checked);
            Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
            Assert.Equal(IssueDuplicatePreflight.GitHubRateLimitFailureCategory, result.ErrorCategory);
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
        Assert.False(result.Preflight.Checked);
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(IssueDuplicatePreflight.GitHubTransportFailureCategory, result.ErrorCategory);
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
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(IssueDuplicatePreflight.GitHubResponseFailureCategory, result.ErrorCategory);
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
        Assert.False(result.Preflight.Checked);
        Assert.Equal(CommandExitCodes.RuntimeError, result.ExitCode);
        Assert.Equal(IssueDuplicatePreflight.GitHubTransportFailureCategory, result.ErrorCategory);
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

    private static string BuildGraphQlIssuePage(
        bool hasNextPage,
        string? endCursor,
        params GraphQlIssueFixture[] issues)
        => JsonSerializer.Serialize(new
        {
            data = new
            {
                repository = new
                {
                    issues = new
                    {
                        nodes = issues.Select(issue => new
                        {
                            id = issue.NodeId,
                            number = issue.Number,
                            title = issue.Title,
                            url = issue.Url ?? $"https://github.example.test/Widthdom/CodeIndex/issues/{issue.Number}",
                            body = issue.Body ?? string.Empty,
                            state = issue.State,
                            labels = new
                            {
                                nodes = (issue.Labels ?? Array.Empty<string>())
                                    .Select(label => new { name = label })
                                    .ToArray(),
                            },
                        }).ToArray(),
                        pageInfo = new
                        {
                            hasNextPage,
                            endCursor,
                        },
                    },
                },
            },
        });

    private static JsonElement ReadGraphQlVariables(RecordedHttpRequest request)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/graphql", request.RequestUri?.ToString());
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body);
        Assert.Contains(
            "query CodeIndexIssueDuplicatePreflight",
            document.RootElement.GetProperty("query").GetString());
        return document.RootElement.GetProperty("variables").Clone();
    }

    private static string[] ReadGraphQlStates(JsonElement variables)
        => variables.GetProperty("states")
            .EnumerateArray()
            .Select(state => state.GetString()!)
            .ToArray();

    public void Dispose()
    {
        IssueDuplicatePreflight.s_httpClientOverride = null;
        _env.Dispose();
        TestProjectHelper.DeleteDirectory(_tempDir);
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

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
            => Task.FromException<int>(exception);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(exception);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record GraphQlIssueFixture(
        int Number,
        string Title,
        string NodeId,
        string State = "OPEN",
        string? Url = null,
        string? Body = null,
        IReadOnlyList<string>? Labels = null);
}
