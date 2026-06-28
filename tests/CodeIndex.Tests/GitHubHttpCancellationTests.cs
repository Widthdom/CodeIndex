using System.Net;
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
