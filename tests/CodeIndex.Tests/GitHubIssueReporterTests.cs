using System.Net;
using System.Text;
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
    // Save original env vars to restore after each test
    // 各テスト後にリストアするため元の環境変数を保存
    private readonly string? _originalCdidxToken;
    private readonly string? _originalGhToken;

    public GitHubIssueReporterTests()
    {
        _originalCdidxToken = Environment.GetEnvironmentVariable("CDIDX_GITHUB_TOKEN");
        _originalGhToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
    }

    [Fact]
    public void ResolveToken_NeitherSet_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        Assert.Null(GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_CdidxTokenSet_ReturnsCdidxToken()
    {
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", "ghp_cdidx_test_token");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);

        Assert.Equal("ghp_cdidx_test_token", GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_GenericGithubToken_IsIgnored()
    {
        // Generic GITHUB_TOKEN must NOT be used — prevents ambient CI tokens
        // from silently publishing to an external repository.
        // 汎用 GITHUB_TOKEN は使わない — CI の環境トークンが意図せず
        // 外部リポジトリに公開されることを防ぐ。
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", null);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "ghp_github_test_token");

        Assert.Null(GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_CdidxTokenSet_IgnoresGenericToken()
    {
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", "ghp_cdidx_preferred");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "ghp_github_fallback");

        Assert.Equal("ghp_cdidx_preferred", GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_EmptyString_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", "");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "");

        Assert.Null(GitHubIssueReporter.ResolveToken());
    }

    [Fact]
    public void ResolveToken_WhitespaceOnly_ReturnsNull()
    {
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", "   ");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "   ");

        Assert.Null(GitHubIssueReporter.ResolveToken());
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
    public void ScrubInlineCode_PreservesPlainText()
    {
        var input = "Symbol extraction misses Kotlin data classes";
        var result = GitHubIssueReporter.ScrubInlineCode(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void ScrubInlineCode_HandlesEmptyAndNull()
    {
        Assert.Equal("", GitHubIssueReporter.ScrubInlineCode(""));
        Assert.Null(GitHubIssueReporter.ScrubInlineCode(null!));
    }

    [Fact]
    public void BuildSubmissionFailureMessage_IsActionable()
    {
        var message = GitHubIssueReporter.BuildSubmissionFailureMessage("network timeout");

        Assert.Contains("GitHub issue creation failed: network timeout", message);
        Assert.Contains("stays recorded locally", message);
        Assert.Contains("CDIDX_GITHUB_TOKEN", message);
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
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

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
    public async Task TryCreateIssueAsync_NoExistingIssue_CreatesNew()
    {
        // Baseline: search returns no items, so the reporter falls through to
        // POST /issues. The PR adds the search step before create — verify it
        // does not break the normal create path.
        // ベースライン: 検索結果が空なら POST /issues に進む。今回追加した検索
        // ステップが通常の作成パスを壊さないことを確認する。
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

        var handler = new RecordingHandler();
        handler.AddResponse(req => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/search/issues",
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = MakeJsonContent("""{ "total_count": 0, "items": [] }"""),
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
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
            Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
        }
    }

    [Fact]
    public async Task TryCreateIssueAsync_SearchApiFails_StillAttemptsCreate()
    {
        // Search-API failure (e.g. 5xx or rate limited) must not block a
        // legitimate first submission. The create POST proceeds as before.
        // 検索 API 失敗（5xx, レート制限など）でも正規の新規送信は阻害しない。
        // 検索失敗時は通常の POST 作成パスに進むこと。
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", "ghp_idempotency_test");

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
            var url = await GitHubIssueReporter.TryCreateIssueAsync(record, "1.0.0-test");

            Assert.Equal("https://github.com/widthdom/CodeIndex/issues/777", url);
            Assert.Equal(2, handler.RequestCount);
        }
        finally
        {
            GitHubIssueReporter.s_httpClientOverride = null;
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
        };
    }

    private static StringContent MakeJsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    public void Dispose()
    {
        // Restore original env vars / 元の環境変数をリストア
        Environment.SetEnvironmentVariable("CDIDX_GITHUB_TOKEN", _originalCdidxToken);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", _originalGhToken);
        // Defensive: never leak the override into other tests.
        // 防御的: オーバーライドを他テストに残さない。
        GitHubIssueReporter.s_httpClientOverride = null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses = new();
        private readonly List<HttpRequestMessage> _requests = new();

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;
        public int RequestCount => _requests.Count;

        public void AddResponse(Func<HttpRequestMessage, bool> match, HttpResponseMessage response)
        {
            _responses.Add((match, response));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);
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
}
