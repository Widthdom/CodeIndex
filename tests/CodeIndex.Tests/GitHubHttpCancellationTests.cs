using System.Net;
using System.Net.Http.Headers;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public sealed class GitHubHttpCancellationTests : IDisposable
{
    private readonly EnvironmentVariableScope env = EnvironmentVariableScope.Capture(
        "CDIDX_GITHUB_TOKEN",
        "CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS",
        GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable);

    public GitHubHttpCancellationTests()
    {
        env.Set("CDIDX_GITHUB_TOKEN", null);
        env.Set("CDIDX_GITHUB_SUBMIT_TIMEOUT_SECONDS", null);
        env.Set(GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable, null);
    }

    [Fact]
    public void GitHubHttpClientFactory_NormalizesInfiniteTimeoutToBoundedMaximum_Issue3954()
    {
        var infiniteBudget = GitHubHttpClientFactory.NormalizeRequestTimeoutBudget(Timeout.InfiniteTimeSpan);
        Assert.True(infiniteBudget.HasTimeout);
        Assert.Equal(GitHubHttpClientFactory.MaxRequestTimeout, infiniteBudget.Duration);

        var overBudget = GitHubHttpClientFactory.NormalizeRequestTimeoutBudget(
            GitHubHttpClientFactory.MaxRequestTimeout + TimeSpan.FromSeconds(1));
        Assert.True(overBudget.HasTimeout);
        Assert.Equal(GitHubHttpClientFactory.MaxRequestTimeout, overBudget.Duration);

        Assert.Equal(
            GitHubHttpClientFactory.MaxRequestTimeout,
            GitHubHttpClientFactory.NormalizeRequestTimeout(Timeout.InfiniteTimeSpan));
        Assert.Equal(
            GitHubHttpClientFactory.MaxRequestTimeout,
            GitHubHttpClientFactory.NormalizeRequestTimeout(GitHubHttpClientFactory.MaxRequestTimeout + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void GitHubHttpClientFactory_ReportsProxyDefaultCredentialsStatusWithoutValue_Issue3954()
    {
        env.Set(GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable, "true");

        Assert.True(GitHubHttpClientFactory.ShouldUseDefaultProxyCredentials());
        Assert.Equal("enabled", GitHubHttpClientFactory.FormatProxyDefaultCredentialsStatus());
    }

    [Fact]
    public void GitHubHttpClientFactory_CreateReleaseDownloadClient_UsesSharedPolicyWithoutApiHeaders_Issue3973()
    {
        env.Set(GitHubHttpClientFactory.ProxyDefaultCredentialsEnvironmentVariable, "true");

        using var handler = GitHubHttpClientFactory.CreateDefaultHttpClientHandler();
        using var client = GitHubHttpClientFactory.CreateReleaseDownloadHttpClient(
            GitHubHttpClientFactory.MaxRequestTimeout + TimeSpan.FromSeconds(1));

        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
        Assert.Equal(GitHubHttpClientFactory.MaxRequestTimeout, client.Timeout);
        Assert.Contains(client.DefaultRequestHeaders.UserAgent, value => value.Product?.Name == "cdidx");
        Assert.Empty(client.DefaultRequestHeaders.Accept);
        Assert.False(client.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"));
    }

    [Fact]
    public async Task GitHubHttpClientFactory_SendWithRetryAsync_RetriesTransientGet_Issue4145()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueException(new HttpRequestException("connection reset"));
        handler.QueueJson(HttpStatusCode.ServiceUnavailable, """{"message":"try later"}""");
        handler.QueueJson(HttpStatusCode.OK, """{"ok":true}""");
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using var response = await GitHubHttpClientFactory.SendWithRetryAsync(
            client,
            static () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Widthdom/CodeIndex/releases/latest");
                GitHubHttpClientFactory.ApplyDefaultHeaders(request.Headers);
                return request;
            },
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GitHubHttpClientFactory.MaxRetryAttempts, handler.RequestCount);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.True(request.HasHeaderValue("User-Agent", "cdidx"));
            Assert.True(request.HasHeaderValue("X-GitHub-Api-Version", "2022-11-28"));
        });
    }

    [Fact]
    public async Task GitHubHttpClientFactory_SendWithRetryAsync_DoesNotRetryRateLimit_Issue4145()
    {
        var handler = new FakeHttpMessageHandler();
        var rateLimited = new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = new StringContent("""{"message":"rate limited"}"""),
        };
        rateLimited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
        handler.QueueResponse(rateLimited);
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using var response = await GitHubHttpClientFactory.SendWithRetryAsync(
            client,
            static () => new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/rate_limit"),
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GitHubHttpClientFactory_SendWithRetryAsync_DoesNotRetryPost_Issue4145()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueJson(HttpStatusCode.ServiceUnavailable, """{"message":"try later"}""");
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        using var response = await GitHubHttpClientFactory.SendWithRetryAsync(
            client,
            static () => new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/repos/Widthdom/CodeIndex/issues")
            {
                Content = new StringContent("{}"),
            },
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task GitHubHttpClientFactory_SendWithRetryAsync_CallerCancellationStopsFakeTimeout_Issue4145()
    {
        var handler = new FakeHttpMessageHandler();
        handler.QueueTimeout();
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitHubHttpClientFactory.SendWithRetryAsync(
                client,
                static () => new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/Widthdom/CodeIndex/releases/latest"),
                HttpCompletionOption.ResponseHeadersRead,
                cts.Token));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UpdateChecker_FetchLatestReleaseTagAsync_RequestTimeoutCancelsPendingSend_Issue3684()
    {
        var handler = new BlockingHttpMessageHandler();
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var fetchTask = UpdateChecker.FetchLatestReleaseTagAsync(
            client,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        await handler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await fetchTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await handler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task IssueDuplicatePreflight_TryLoadAsync_CallerCancellationCancelsPendingSend_Issue3684()
    {
        var handler = new BlockingHttpMessageHandler();
        using var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        IssueDuplicatePreflight.s_httpClientOverride = client;
        using var cts = new CancellationTokenSource();

        var loadTask = IssueDuplicatePreflight.TryLoadAsync("github", "Widthdom/CodeIndex", cts.Token);

        await handler.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await loadTask.WaitAsync(TimeSpan.FromSeconds(5)));
        await handler.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        IssueDuplicatePreflight.s_httpClientOverride = null;
        env.Dispose();
    }

    private sealed class BlockingHttpMessageHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> responseSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> sendStartedSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> cancellationObservedSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task SendStarted => sendStartedSource.Task;

        internal Task CancellationObserved => cancellationObservedSource.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            sendStartedSource.TrySetResult(null);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
                cancellationObservedSource);
            try
            {
                return await responseSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancellationObservedSource.TrySetResult(null);
                throw;
            }
        }
    }
}
