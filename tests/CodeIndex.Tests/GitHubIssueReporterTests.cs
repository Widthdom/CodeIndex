using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for GitHubIssueReporter token resolution logic.
/// GitHubIssueReporter のトークン解決ロジックのテスト。
///
/// Note: The actual GitHub API call is not tested here (would require
/// a real token and network access). Only the token resolution logic
/// is covered.
/// 注: 実際の GitHub API 呼び出しはここではテストしない（実トークンと
/// ネットワークアクセスが必要）。トークン解決ロジックのみをカバーする。
/// </summary>
[Collection("SQLite pool sensitive")]
public class GitHubIssueReporterTests : IDisposable
{
    private readonly EnvironmentVariableScope _env = EnvironmentVariableScope.Capture(
        "CDIDX_GITHUB_TOKEN",
        "GITHUB_TOKEN",
        "CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS",
        GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable);

    [Fact]
    public void ResolveToken_NeitherSet_ReturnsNull()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", null);
        _env.Set("GITHUB_TOKEN", null);

        Assert.Null(GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_CdidxTokenSet_ReturnsCdidxToken()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_cdidx_test_token");
        _env.Set("GITHUB_TOKEN", null);

        Assert.Equal("ghp_cdidx_test_token", GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_GenericGithubToken_IsIgnored()
    {
        // Generic GITHUB_TOKEN must NOT be used — prevents ambient CI tokens
        // from silently publishing to an external repository.
        // 汎用 GITHUB_TOKEN は使わない — CI の環境トークンが意図せず
        // 外部リポジトリに公開されることを防ぐ。
        _env.Set("CDIDX_GITHUB_TOKEN", null);
        _env.Set("GITHUB_TOKEN", "ghp_github_test_token");

        Assert.Null(GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_CdidxTokenSet_IgnoresGenericToken()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_cdidx_preferred");
        _env.Set("GITHUB_TOKEN", "ghp_github_fallback");

        Assert.Equal("ghp_cdidx_preferred", GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_EmptyString_ReturnsNull()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "");
        _env.Set("GITHUB_TOKEN", "");

        Assert.Null(GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_WhitespaceOnly_ReturnsNull()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "   ");
        _env.Set("GITHUB_TOKEN", "   ");

        Assert.Null(GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveSubmitTimeout_UsesPositiveEnvironmentOverride()
    {
        _env.Set("CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS", "3");

        Assert.Equal(TimeSpan.FromSeconds(3), GitHubIssueReporter.ResolveSubmitTimeout());
    }

    [Fact]
    public void ResolveSubmitTimeout_InvalidOverride_UsesDefault()
    {
        _env.Set("CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS", "not-a-number");

        Assert.Equal(GitHubIssueReporter.DefaultTimeout, GitHubIssueReporter.ResolveSubmitTimeout());
    }

    [Fact]
    public void ResolveSubmitTimeout_AboveMaximum_UsesDefault()
    {
        var tooLarge = GitHubIssueReporter.MaxSubmitTimeoutSeconds + 1;
        _env.Set("CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS", tooLarge.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(GitHubIssueReporter.DefaultTimeout, GitHubIssueReporter.ResolveSubmitTimeout());
    }

    [Fact]
    public void DefaultHttpClient_UsesExplicitTimeout()
    {
        var field = typeof(GitHubIssueReporter).GetField("s_defaultHttpClient", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        var client = Assert.IsType<HttpClient>(field.GetValue(null));
        Assert.Equal(TimeSpan.FromSeconds(GitHubIssueReporter.MaxSubmitTimeoutSeconds), client.Timeout);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void ResolveSubmitTimeout_IgnoresCurrentCulturePositiveSign_Issue3404()
    {
        using var _ = new CultureScope(TestCultures.BuildCaretPositiveSignCulture());
        _env.Set("CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS", "^3");

        Assert.Equal(GitHubIssueReporter.DefaultTimeout, GitHubIssueReporter.ResolveSubmitTimeout());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" yes ", true)]
    [InlineData("yes", true)]
    public void ShouldUseDefaultProxyCredentials_IsExplicitOptIn_Issue3369(string? value, bool expected)
    {
        _env.Set(GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable, value);

        Assert.Equal(expected, GitHubHttpClientFactory.ShouldUseDefaultProxyCredentials());
    }

    // --- ScrubInlineCode tests / ScrubInlineCode テスト ---

    [Fact]
    public void ScrubInlineCode_RemovesBacktickSpans()
    {
        var input = "Arrow functions like `const foo = () => {}` are not detected";
        var result = GitHubIssueReporter.ScrubInlineCode(input);
        Assert.Equal("Arrow functions like [code example removed] are not detected", result);
    }

    [Fact]
    public void ScrubInlineCode_RemovesMultipleSpans()
    {
        var input = "Both `import React` and `require('foo')` are missed";
        var result = GitHubIssueReporter.ScrubInlineCode(input);
        Assert.Equal("Both [code example removed] and [code example removed] are missed", result);
    }

    [Fact]
    public void ScrubInlineCode_RemovesInlineSpanContainingNestedBackticks()
    {
        var input = "Template examples like `const x = `template`` should not leak";
        var result = GitHubIssueReporter.ScrubInlineCode(input);
        Assert.Equal("Template examples like [code example removed] should not leak", result);
        Assert.DoesNotContain("template", result);
    }

    [Fact]
    public void ScrubInlineCode_IgnoresEscapedBackticksInsideInlineSpan()
    {
        var input = "Escaped examples like `const x = \\`secret\\`` should not leak";
        var result = GitHubIssueReporter.ScrubInlineCode(input);
        Assert.Equal("Escaped examples like [code example removed] should not leak", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public void ScrubInlineCode_RemovesInlineSpanFollowedByLetter()
    {
        var input = "Call `secret()`when submitting";
        var result = GitHubIssueReporter.ScrubInlineCode(input);
        Assert.Equal("Call [code example removed]when submitting", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public void ScrubInlineCode_RemovesInlineSpanWithTrailingSpaceBeforeAdjacentText()
    {
        var input = "Use `secret `and retry";
        var result = GitHubIssueReporter.ScrubInlineCode(input);
        Assert.Equal("Use [code example removed]and retry", result);
        Assert.DoesNotContain("secret", result);
    }

    [Fact]
    public void ScrubInlineCode_PreservesPlainText()
    {
        var input = "Symbol extraction misses Kotlin data classes";
        var result = GitHubIssueReporter.ScrubInlineCode(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ScrubInlineCode_StripsTripleBacktickFences()
    {
        var input = """
        Before
        ```
        secret();
        ```
        After
        """;

        var result = GitHubIssueReporter.ScrubInlineCode(input);

        Assert.Equal("""
        Before
        [code example removed]
        After
        """, result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ScrubInlineCode_StripsLanguageLabeledFences()
    {
        var input = """
        Before
        ```csharp
        secret();
        ```
        After
        """;

        var result = GitHubIssueReporter.ScrubInlineCode(input);

        Assert.Equal("""
        Before
        [code example removed]
        After
        """, result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ScrubInlineCode_StripsIndentedFences()
    {
        var input = """
        Before
            ```
            secret();
            ```
        After
        """;

        var result = GitHubIssueReporter.ScrubInlineCode(input);

        Assert.Equal("""
        Before
            [code example removed]
        After
        """, result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ScrubInlineCode_StripsMixedFenceAndInline()
    {
        var input = """
        Before `inlineSecret()`
        ```csharp
        fencedSecret();
        ```
        After
        """;

        var result = GitHubIssueReporter.ScrubInlineCode(input);

        Assert.Equal("""
        Before [code example removed]
        [code example removed]
        After
        """, result);
        Assert.DoesNotContain("inlineSecret", result);
        Assert.DoesNotContain("fencedSecret", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ScrubInlineCode_HandlesEmptyAndNull()
    {
        Assert.Equal("", GitHubIssueReporter.ScrubInlineCode(""));
        Assert.Null(GitHubIssueReporter.ScrubInlineCode(null!));
    }

    [Fact]
    public void ScrubInlineCode_BoundsLargePlainTextInput()
    {
        var input = new string('a', GitHubIssueReporter.MaxScrubInputLength + 1024);

        var result = GitHubIssueReporter.ScrubInlineCode(input);

        Assert.Equal(GitHubIssueReporter.MaxScrubInputLength + "\n[truncated]".Length, result.Length);
        Assert.EndsWith("\n[truncated]", result);
    }

    [Fact]
    public void ScrubInlineCode_BoundsLargeUnclosedFenceAndDoesNotLeakCode()
    {
        var input = "Before\n```csharp\nsecret();\n" + new string('x', GitHubIssueReporter.MaxScrubInputLength + 1024);

        var result = GitHubIssueReporter.ScrubInlineCode(input);

        Assert.Equal("Before\n[code example removed]\n[truncated]", result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("```", result);
    }

    [Fact]
    public void ScrubInlineCode_BoundsLargeUnclosedInlineSpanAndDoesNotLeakCode()
    {
        var input = "Before `secret()" + new string('x', GitHubIssueReporter.MaxScrubInputLength + 1024);

        var result = GitHubIssueReporter.ScrubInlineCode(input);

        Assert.Equal("Before [code example removed]\n[truncated]", result);
        Assert.DoesNotContain("secret", result);
        Assert.DoesNotContain("`", result);
    }

    [Fact]
    public void BuildSubmissionFailureMessage_IsActionable()
    {
        var message = GitHubIssueReporter.BuildSubmissionFailureMessage("network timeout");

        Assert.Contains("GitHub issue creation failed: network timeout", message);
        Assert.Contains("stays recorded locally", message);
        Assert.Contains("CDIDX_GITHUB_TOKEN", message);
        Assert.Contains("HTTPS_PROXY", message);
        Assert.Contains("retry `suggest_improvement`", message);
    }

    [Fact]
    public void BuildApiFailureMessage_IsActionable()
    {
        var message = GitHubIssueReporter.BuildApiFailureMessage(403, "{\"message\":\"forbidden\"}");

        Assert.Contains("GitHub API responded 403", message);
        Assert.Contains("stays local", message);
        Assert.Contains("repository permissions", message);
        Assert.Contains("retry `suggest_improvement`", message);
    }

    [Fact]
    public void BuildApiFailureMessage_RedactsAndBoundsSensitiveErrorBody()
    {
        var errorBody = $$"""
        {
            "message": "validation failed",
            "token": "value-that-must-not-leak",
            "details": "{{new string('x', 2000)}}"
        }
        """;

        var message = GitHubIssueReporter.BuildApiFailureMessage(403, errorBody);

        Assert.DoesNotContain("value-that-must-not-leak", message);
        Assert.Contains("\"token\":\"[redacted]\"", message);
        Assert.True(message.Length < 900);
    }

    [Fact]
    public void BuildRateLimitFailureMessage_RedactsSensitiveErrorBody()
    {
        var retryAt = new DateTime(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

        var message = GitHubIssueReporter.BuildRateLimitFailureMessage(
            429,
            """{ "authorization": "Bearer value-that-must-not-leak", "message": "rate limited" }""",
            retryAt);

        Assert.DoesNotContain("value-that-must-not-leak", message);
        Assert.Contains("\"authorization\":\"[redacted]\"", message);
        Assert.Contains(retryAt.ToString("O"), message);
    }

    [Fact]
    public void BuildIssueLabels_MapsSuggestionCategoriesToExistingRepositoryLabels()
    {
        Assert.Equal(["enhancement"], GitHubIssueReporter.BuildIssueLabels(new SuggestionRecord { Category = "output_format" }));
        Assert.Equal(["bug"], GitHubIssueReporter.BuildIssueLabels(new SuggestionRecord { Category = "crash_report" }));
        Assert.Equal(["bug"], GitHubIssueReporter.BuildIssueLabels(new SuggestionRecord { Category = "unexpected_error" }));
    }

    [Fact]
    public void BuildApiErrorDetail_UsesStatusAndSingleLineBodyExcerpt()
    {
        var detail = GitHubIssueReporter.BuildApiErrorDetail(422, "{\n\"message\":\"validation failed\"\n}");

        Assert.Equal("422: { \"message\":\"validation failed\" }", detail);
    }

    [Fact]
    public void BuildIssueTitle_ClampsFinalTitleToGitHubLimit()
    {
        var category = new string('c', 240);
        var title = GitHubIssueReporter.BuildIssueTitle(category, new string('d', 200));

        Assert.True(title.Length <= GitHubIssueReporter.MaxGitHubIssueTitleLength);
    }

    [Fact]
    public void BuildIssueTitle_StripsMarkdownSyntaxFromTitleSource()
    {
        var title = GitHubIssueReporter.BuildIssueTitle(
            "other](https://example.com/very-long-category-name-that-should-not-expand-forever)",
            "   ](https://example.com/x) ![spoofed](https://example.com/y) `secret()` gap");

        Assert.StartsWith("[AI Suggestion] otherhttps://example.com/very-long-categ: ", title);
        var titleSource = title["[AI Suggestion] ".Length..];
        Assert.DoesNotContain("[", titleSource);
        Assert.DoesNotContain("]", titleSource);
        Assert.DoesNotContain("(", titleSource);
        Assert.DoesNotContain(")", titleSource);
        Assert.DoesNotContain("`", titleSource);
        Assert.DoesNotContain("secret", titleSource);
        Assert.DoesNotContain("  :", titleSource);
    }

    [Fact]
    public async Task TryCreateIssueAsync_PostPayloadTitleDoesNotExceedGitHubLimit()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_title_length_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/4242" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var description = new string('d', 500);
            var record = MakeRecordWithKnownHash();
            record.Category = new string('c', 240);
            record.Description = description;
            record.Hash = SuggestionStore.ComputeHash(record.Category, record.Language, description);

            await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            var postedJson = Assert.Single(handler.RequestBodies);
            var payload = JsonNode.Parse(postedJson)!.AsObject();
            var title = payload["title"]!.GetValue<string>();
            Assert.True(title.Length <= GitHubIssueReporter.MaxGitHubIssueTitleLength);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public void GetRateLimitRetryAt_UsesRetryAfterDeltaFor429()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        var now = new DateTime(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc);

        var retryAt = GitHubIssueReporter.GetRateLimitRetryAt(response, now);

        Assert.Equal(now.AddSeconds(60), retryAt);
    }

    [Fact]
    public void GetRateLimitRetryAt_UsesResetHeaderForForbiddenExhaustedLimit()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
        response.Headers.TryAddWithoutValidation("x-ratelimit-reset", "1770000000");

        var retryAt = GitHubIssueReporter.GetRateLimitRetryAt(response, new DateTime(2026, 5, 23, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1770000000).UtcDateTime, retryAt);
    }

    // --- Idempotency-on-retry tests / 再試行時の冪等性テスト ---

    [Fact]
    public async Task TryCreateIssueAsync_FindsExistingIssue_DoesNotCreateDuplicate()
    {
        // Simulates the failure mode from #1878: a previous submission attempt
        // created an issue on GitHub but the response was lost in transit,
        // leaving SubmittedToGitHub=false locally. On retry, the search-by-hash
        // idempotency check must find the existing issue and short-circuit so
        // a duplicate is not created.
        // #1878 の障害モードを再現: 過去の送信で GitHub 側に Issue が作成された
        // がレスポンスが消失し、ローカルでは SubmittedToGitHub=false のまま。
        // 再試行時はハッシュ検索による冪等性チェックが既存 Issue を見つけ、
        // 重複作成を回避すること。
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""
                {
                    "total_count": 1,
                    "items": [
                        { "html_url": "https://github.com/widthdom/CodeIndex/issues/9999" }
                    ]
                }
                """),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var url = await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/9999", url);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueAsync_SearchMissesButLabelListFindsExistingIssue_DoesNotCreateDuplicate()
    {
        // GitHub Search can lag behind newly-created issues. When Search
        // returns no items, the direct labeled issue list must still catch an
        // issue that already exists on GitHub and carries the suggestion hash.
        // GitHub Search は作成直後の Issue を反映するまで遅延し得る。
        // Search が空でも、label 付き Issue の直接一覧で提案ハッシュを持つ
        // 既存 Issue を検出し、重複作成を防ぐこと。
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var record = MakeRecordWithKnownHash();
        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent($$"""
                [
                    {
                        "html_url": "https://github.com/widthdom/CodeIndex/issues/2468",
                        "body": "Submitted by cdidx. Hash: `{{record.Hash}}`"
                    }
                ]
                """),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var url = await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/2468", url);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.Equal("/search/issues", handler.Requests[0].RequestUri!.AbsolutePath);
            Assert.Equal("/repos/widthdom/CodeIndex/issues", handler.Requests[1].RequestUri!.AbsolutePath);
            Assert.Contains("labels=enhancement", handler.Requests[1].RequestUri!.Query);
            Assert.Contains("state=open", handler.Requests[1].RequestUri!.Query);
            Assert.DoesNotContain("state=all", handler.Requests[1].RequestUri!.Query);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueAsync_DuplicateLookupIgnoresClosedIssuesAndPullRequests()
    {
        // Closed history and PRs can mention the same suggestion hash, but only
        // open Issues should suppress a new actionable submission.
        // closed 済み履歴や PR が同じ提案ハッシュを含んでも、新規送信を抑止するのは
        // open Issue のみであること。
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var record = MakeRecordWithKnownHash();
        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent($$"""
                {
                    "total_count": 2,
                    "items": [
                        {
                            "html_url": "https://github.com/widthdom/CodeIndex/issues/1357",
                            "state": "closed",
                            "body": "Submitted by cdidx. Hash: `{{record.Hash}}`"
                        },
                        {
                            "html_url": "https://github.com/widthdom/CodeIndex/pull/2468",
                            "state": "open",
                            "pull_request": {},
                            "body": "Submitted by cdidx. Hash: `{{record.Hash}}`"
                        }
                    ]
                }
                """),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent($$"""
                [
                    {
                        "html_url": "https://github.com/widthdom/CodeIndex/pull/9753",
                        "state": "open",
                        "pull_request": {},
                        "body": "Submitted by cdidx. Hash: `{{record.Hash}}`"
                    }
                ]
                """),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/12345" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var url = await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/12345", url);
            Assert.Equal(3, handler.RequestCount);

            var searchQuery = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query);
            Assert.Contains("is:issue", searchQuery);
            Assert.Contains("is:open", searchQuery);
            Assert.Contains("in:body", searchQuery);
            Assert.Contains("state=open", handler.Requests[1].RequestUri!.Query);
            Assert.DoesNotContain("state=all", handler.Requests[1].RequestUri!.Query);
            Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueAsync_NoExistingIssue_CreatesNew()
    {
        // Baseline: search returns no items, so the reporter falls through to
        // POST /issues. The PR adds the search step before create — verify it
        // does not break the normal create path.
        // ベースライン: 検索結果が空なら POST /issues に進む。今回追加した検索
        // ステップが通常の作成パスを壊さないことを確認する。
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/12345" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var url = await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/12345", url);
            Assert.Equal(3, handler.RequestCount);
            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
            Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
            Assert.All(handler.Requests, request => AssertUsesGitHubApiHeaders(request, "ghp_idempotency_test"));
            var postedJson = Assert.Single(handler.RequestBodies);
            Assert.Contains("codex/5.0", postedJson);
            Assert.Contains("session-123", postedJson);
            Assert.Contains("MCP client: codex", postedJson);
            Assert.Contains("Tool invocation context: Investigating suggestion triage", postedJson);
            var payload = JsonNode.Parse(postedJson)!;
            Assert.Equal("enhancement", payload["labels"]![0]!.GetValue<string>());
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_SearchFailureFailsClosedWithoutCreate()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = MakeJsonContent("""{ "message": "search unavailable" }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/12345" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("existing-suggestion lookup failed during search", result.Error);
            Assert.Contains("502", result.Error);
            Assert.Equal(1, handler.RequestCount);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_LabelListFailureFailsClosedWithoutCreate()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = MakeJsonContent("""{ "message": "issue list unavailable" }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/12345" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("existing-suggestion lookup failed during label list", result.Error);
            Assert.Contains("503", result.Error);
            Assert.Equal(2, handler.RequestCount);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_SearchMissingItemsFailsClosedWithoutCreate()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0 }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/12345" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("existing-suggestion lookup failed during search", result.Error);
            Assert.Contains("missing items array", result.Error);
            Assert.Equal(1, handler.RequestCount);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_LabelListObjectFailsClosedWithoutCreate()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "message": "not an array" }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/12345" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("existing-suggestion lookup failed during label list", result.Error);
            Assert.Contains("expected issue array", result.Error);
            Assert.Equal(2, handler.RequestCount);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_CreateSuccessBodyOverLimit_ReturnsDiagnosticError()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new ByteArrayContent(new byte[GitHubIssueReporter.MaxGitHubApiResponseBodyBytes + 1]),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Equal("InvalidDataException", result.Error);
            Assert.DoesNotContain("HTTP response body exceeded", result.Error);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_SearchBodyOverLimit_FailsClosedWithoutCreate()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[GitHubIssueReporter.MaxGitHubApiResponseBodyBytes + 1]),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/3333" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("existing-suggestion lookup failed during search", result.Error);
            Assert.Contains("InvalidDataException", result.Error);
            Assert.DoesNotContain("HTTP response body exceeded", result.Error);
            Assert.Equal(1, handler.RequestCount);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueAsync_SearchSuccessDoesNotPrebufferResponseContent()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new NonBufferingJsonContent("""
                {
                    "total_count": 1,
                    "items": [
                        { "html_url": "https://github.com/widthdom/CodeIndex/issues/3335" }
                    ]
                }
                """),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var url = await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/3335", url);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_LabelListJsonOverDepthLimit_FailsClosedWithoutCreate()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent(MakeDeepObjectJson(GitHubIssueReporter.MaxGitHubApiResponseJsonDepth + 8)),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/3334" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("existing-suggestion lookup failed during label list", result.Error);
            Assert.Contains("JsonReaderException", result.Error);
            Assert.DoesNotContain("maximum configured depth", result.Error);
            Assert.Equal(2, handler.RequestCount);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueAsync_LabelListSuccessDoesNotPrebufferResponseContent()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var record = MakeRecordWithKnownHash();
        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new NonBufferingJsonContent($$"""
                [
                    {
                        "html_url": "https://github.com/widthdom/CodeIndex/issues/3336",
                        "body": "Submitted by cdidx. Hash: `{{record.Hash}}`"
                    }
                ]
                """),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var url = await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/3336", url);
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public void TryCreateIssueAsync_LabelListStopsAtConfiguredPageCapAndWarns()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        for (var page = 1; page <= GitHubIssueReporter.MaxExistingSuggestionLookupPagesPerLabel; page++)
        {
            var pageNumber = page;
            handler.AddResponse(
                req => req.Method == HttpMethod.Get
                    && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues"
                    && req.RequestUri!.Query.Contains($"&page={pageNumber}", StringComparison.Ordinal),
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = MakeJsonContent(MakeIssueListJson(100, "Submitted by cdidx. Hash: `not-this-hash`")),
                });
        }
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/4444" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        var originalError = Console.Error;
        using var capturedError = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            string? url;
            lock (TestConsoleLock.Gate)
            {
                Console.SetError(capturedError);
                try
                {
                    var record = MakeRecordWithKnownHash();
#pragma warning disable xUnit1031
                    url = GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test").GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(originalError);
                }
            }

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/4444", url);
            Assert.Equal(GitHubIssueReporter.MaxExistingSuggestionLookupPagesPerLabel + 2, handler.RequestCount);
            Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.Query.Contains(
                $"&page={GitHubIssueReporter.MaxExistingSuggestionLookupPagesPerLabel + 1}",
                StringComparison.Ordinal));
            Assert.Contains("existing-suggestion lookup reached", capturedError.ToString());
            Assert.Equal(HttpMethod.Post, handler.Requests[^1].Method);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_CreateSuccessJsonOverDepthLimit_ReturnsDiagnosticError()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent(MakeDeepObjectJson(GitHubIssueReporter.MaxGitHubApiResponseJsonDepth + 8)),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("Json", result.Error);
            Assert.DoesNotContain("maximum configured depth", result.Error);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueAsync_CreateSuccessDoesNotPrebufferResponseContent()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new NonBufferingJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/3337" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var url = await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/3337", url);
            Assert.Equal(3, handler.RequestCount);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_SearchApiFails_FailsClosedWithoutCreate()
    {
        // Search-API failure (e.g. 5xx or rate limited) makes the duplicate
        // lookup indeterminate, so the reporter must not create a possible duplicate.
        // 検索 API 失敗（5xx, レート制限など）では重複 lookup が不確定になるため、
        // 重複の可能性がある Issue 作成には進まないこと。
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = MakeJsonContent("""{ "message": "service unavailable" }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/777" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("existing-suggestion lookup failed during search", result.Error);
            Assert.Contains("503", result.Error);
            Assert.Equal(1, handler.RequestCount);
            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_CreateApiFails_ReturnsDiagnosticError()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = MakeJsonContent("""{ "message": "validation failed" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Equal("""422: { "message": "validation failed" }""", result.Error);
            Assert.Equal(3, handler.RequestCount);
            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
            Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_CreateApiFails_ReadsBoundedSanitizedErrorBody()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var errorBody = "{\"message\":\"validation failed\",\"token\":\"value-that-must-not-leak\",\"details\":\""
            + new string('x', GitHubIssueReporter.MaxGitHubApiErrorBodyBytes * 2)
            + "\"}";

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(errorBody)),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.DoesNotContain("value-that-must-not-leak", result.Error);
            Assert.Contains("[redacted]", result.Error);
            Assert.True(result.Error!.Length <= 512);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task ReadBoundedApiErrorBodyAsync_StopsAfterByteCap()
    {
        var errorBody = Encoding.UTF8.GetBytes(
            "prefix " + new string('x', GitHubIssueReporter.MaxGitHubApiErrorBodyBytes * 2));
        var stream = new ThrowIfOverReadStream(
            errorBody,
            GitHubIssueReporter.MaxGitHubApiErrorBodyBytes + 1);

        var result = await GitHubIssueReporter.ReadBoundedApiErrorBodyAsync(stream, CancellationToken.None);

        Assert.True(stream.BytesRead <= GitHubIssueReporter.MaxGitHubApiErrorBodyBytes + 1);
        Assert.EndsWith(" [response body truncated]", result);
    }

    [Fact]
    public async Task ReadGitHubApiResponseJsonAsync_RejectsBodyOverLimit()
    {
        using var content = new ByteArrayContent(new byte[GitHubIssueReporter.MaxGitHubApiResponseBodyBytes + 1]);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => GitHubIssueReporter.ReadGitHubApiResponseJsonAsync(content, CancellationToken.None));

        Assert.Contains("HTTP response body exceeded", ex.Message);
    }

    [Fact]
    public async Task ReadGitHubApiResponseJsonAsync_RejectsJsonOverDepthLimit()
    {
        using var content = MakeJsonContent(MakeDeepObjectJson(GitHubIssueReporter.MaxGitHubApiResponseJsonDepth + 8));

        await Assert.ThrowsAnyAsync<JsonException>(
            () => GitHubIssueReporter.ReadGitHubApiResponseJsonAsync(content, CancellationToken.None));
    }

    [Fact]
    public void BuildApiErrorDetail_DeepJsonUsesRegexFallbackWithoutLeakingSecret()
    {
        var errorBody = "{\"token\":\"value-that-must-not-leak\",\"nested\":"
            + MakeDeepObjectJson(GitHubIssueReporter.MaxGitHubApiResponseJsonDepth + 8)
            + "}";

        var detail = GitHubIssueReporter.BuildApiErrorDetail(500, errorBody);

        Assert.DoesNotContain("value-that-must-not-leak", detail);
        Assert.Contains("[redacted]", detail);
    }

    [Fact]
    public void BuildApiErrorDetail_BoundsRedactedJsonSerialization_Issue3740()
    {
        var secret = "value-that-must-not-leak";
        var errorBody = "{\"token\":\"" + secret + "\",\"message\":\"" + new string('\u6f22', 1200) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(errorBody) <= GitHubIssueReporter.MaxGitHubApiErrorBodyBytes);

        var detail = GitHubIssueReporter.BuildApiErrorDetail(500, errorBody);

        Assert.True(
            detail.Length <= "500: ".Length + 500,
            detail.Length.ToString(CultureInfo.InvariantCulture));
        Assert.StartsWith("500: {\"token\":\"[redacted]\",\"message\":\"", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, detail);
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_UserCancellation_Propagates()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var mockClient = new HttpClient(new ThrowingHandler(
            new TaskCanceledException("user canceled", null, cts.Token)));
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();

            await Assert.ThrowsAsync<TaskCanceledException>(
                () => GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test"));
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_TimeoutCancellation_ReturnsDiagnosticError()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var mockClient = new HttpClient(new ThrowingHandler(new TaskCanceledException(
            "request timed out",
            new TimeoutException("HTTP timeout"),
            cts.Token)));
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Equal("TaskCanceledException", result.Error);
            Assert.DoesNotContain("request timed out", result.Error);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_ConfiguredTimeout_ReturnsDiagnosticError()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");
        _env.Set("CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS", "1");

        using var mockClient = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(10)));
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Equal("TaskCanceledException: GitHub submission timed out after 1 seconds", result.Error);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }


    [Fact]
    public async Task TryCreateIssueDetailedAsync_OutOfMemoryException_Propagates()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        using var mockClient = new HttpClient(new ThrowingHandler(
            new OutOfMemoryException("allocation failed")));
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var record = MakeRecordWithKnownHash();

            await Assert.ThrowsAsync<OutOfMemoryException>(
                () => GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test"));
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_RateLimited_ReturnsRetryAfterDiagnostic()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            });
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/repos/widthdom/CodeIndex/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("[]"),
            });
        var rateLimitResponse = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = MakeJsonContent("""{ "message": "rate limited" }"""),
        };
        rateLimitResponse.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            rateLimitResponse);
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        var fixedNow = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);
        GitHubIssueReporter.TimeProvider = new ManualTimeProvider(fixedNow);
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("429", result.Error);
            Assert.Contains("next_retry_at=", result.Error);
            Assert.NotNull(result.NextRetryAt);
            Assert.Equal(fixedNow.UtcDateTime.AddSeconds(60), result.NextRetryAt.Value);
            Assert.Equal(3, handler.RequestCount);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
            GitHubIssueReporter.TimeProvider = TimeProvider.System;
        }
    }

    [Fact]
    public async Task TryCreateIssueDetailedAsync_SearchRateLimitIncludesRetryMetadata_Issue3823()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        var response = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = MakeJsonContent("""{ "message": "rate limited", "token": "secret-value" }"""),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues", response);
        handler.AddResponse(req => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/issues"),
            new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = MakeJsonContent("""{ "html_url": "https://github.com/widthdom/CodeIndex/issues/9999" }"""),
            });
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        var fixedNow = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);
        GitHubIssueReporter.TimeProvider = new ManualTimeProvider(fixedNow);
        try
        {
            var record = MakeRecordWithKnownHash();
            var result = await GitHubIssueReporter.TryCreateIssueDetailedAsync(record, "1.0.0-test");

            Assert.Null(result.IssueUrl);
            Assert.Contains("existing-suggestion lookup failed during search", result.Error);
            Assert.Contains("429", result.Error);
            Assert.Contains("next_retry_at=", result.Error);
            Assert.Contains(fixedNow.UtcDateTime.AddSeconds(30).ToString("O", CultureInfo.InvariantCulture), result.Error);
            Assert.DoesNotContain("secret-value", result.Error);
            Assert.Equal(1, handler.RequestCount);
            Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
            GitHubIssueReporter.TimeProvider = TimeProvider.System;
        }
    }

    [Fact]
    public void TryCreateIssueDetailedAsync_SubmissionExceptionIsSanitized_Issue3823()
    {
        _env.Set("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        using var mockClient = new HttpClient(new ThrowingHandler(new HttpRequestException("secret host detail")));
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        var originalError = Console.Error;
        using var capturedError = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            SuggestionStore.SubmitAttemptResult result;
            lock (TestConsoleLock.Gate)
            {
                Console.SetError(capturedError);
                try
                {
#pragma warning disable xUnit1031
                    result = GitHubIssueReporter.TryCreateIssueDetailedAsync(
                            MakeRecordWithKnownHash(),
                            "1.0.0-test")
                        .GetAwaiter()
                        .GetResult();
#pragma warning restore xUnit1031
                }
                finally
                {
                    Console.SetError(originalError);
                }
            }

            Assert.Null(result.IssueUrl);
            Assert.Equal("HttpRequestException", result.Error);
            Assert.Contains("HttpRequestException", capturedError.ToString());
            Assert.DoesNotContain("secret host detail", capturedError.ToString());
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task FindExistingIssueByHashAsync_NonHexHash_ReturnsNullWithoutCallingApi()
    {
        // Defensive: only hex hashes are passed to the search query so that
        // arbitrary text cannot inject GitHub search operators.
        // 防御的: GitHub 検索演算子を注入できないよう、16進ハッシュのみで検索。
        var handler = new RecordingHandler();
        using var mockClient = new HttpClient(handler);
        GitHubIssueReporter.s_httpClientOverride = mockClient;
        try
        {
            var url = await GitHubIssueReporter.FindExistingIssueByHashAsync("not-a-hex-hash", "ghp_test");
            Assert.Null(url);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    private static SuggestionRecord MakeRecordWithKnownHash()
    {
        var description = "Idempotency retry regression for #1878";
        return new SuggestionRecord
        {
            Category = "other",
            Language = null,
            Description = description,
            Hash = SuggestionStore.ComputeHash("other", null, description),
            CreatedAt = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc),
            CreatedByAgent = "codex/5.0",
            SessionId = "session-123",
            ClientVersion = "1.0.0-test",
            McpClientName = "codex",
            McpClientVersion = "5.0",
            ToolInvocationContext = "Investigating suggestion triage",
        };
    }

    private static StringContent MakeJsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static void AssertUsesGitHubApiHeaders(HttpRequestMessage request, string token)
    {
        Assert.Contains(request.Headers.UserAgent, value => value.Product?.Name == "cdidx");
        Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/vnd.github+json");
        Assert.True(request.Headers.TryGetValues("X-GitHub-Api-Version", out var values));
        Assert.Contains("2022-11-28", values);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(token, request.Headers.Authorization?.Parameter);
    }

    private static string MakeDeepObjectJson(int depth)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < depth; i++)
            builder.Append("{\"x\":");
        builder.Append('0');
        for (var i = 0; i < depth; i++)
            builder.Append('}');
        return builder.ToString();
    }

    private static string MakeIssueListJson(int count, string body)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append("{\"html_url\":\"https://github.com/widthdom/CodeIndex/issues/");
            builder.Append(1000 + i);
            builder.Append("\",\"state\":\"open\",\"body\":");
            builder.Append(JsonSerializer.Serialize(body));
            builder.Append('}');
        }
        builder.Append(']');
        return builder.ToString();
    }

    public void Dispose()
    {
        _env.Dispose();
        // Defensive: never leak the override into other tests.
        // 防御的: オーバーライドを他テストに残さない。
        GitHubIssueReporter.s_httpClientOverride = null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses = new();
        private readonly List<HttpRequestMessage> _requests = new();
        private readonly List<string> _requestBodies = new();

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;
        public IReadOnlyList<string> RequestBodies => _requestBodies;
        public int RequestCount => _requests.Count;

        public void AddResponse(Func<HttpRequestMessage, bool> match, HttpResponseMessage response)
        {
            _responses.Add((match, response));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
            if (request.Content != null)
                _requestBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            foreach (var entry in _responses)
            {
                if (entry.Match(request))
                    return Task.FromResult(entry.Response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private sealed class NonBufferingJsonContent : HttpContent
    {
        private readonly byte[] _payload;

        internal NonBufferingJsonContent(string json)
        {
            _payload = Encoding.UTF8.GetBytes(json);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new InvalidOperationException("Response content was pre-buffered."));

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Response content was pre-buffered."));

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(_payload, writable: false));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(_payload, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }

    private sealed class ThrowIfOverReadStream(byte[] data, int maxBytesRead) : Stream
    {
        private int _position;

        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            if (_position >= data.Length)
                return 0;

            var count = Math.Min(destination.Length, data.Length - _position);
            if (BytesRead + count > maxBytesRead)
                throw new InvalidOperationException("Response body read exceeded bounded limit.");

            data.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
            };
        }
    }
}
