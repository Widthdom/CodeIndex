using System.Net;
using System.Net.Http.Headers;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

internal static class GitHubHttpClientFactory
{
    internal const string ProxyDefaultCredentialsEnvironmentVariable = "CDIDX_GITHUB_PROXY_USE_DEFAULT_CREDENTIALS";
    internal static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMinutes(5);
    private const string GitHubApiVersionHeader = "X-GitHub-Api-Version";
    private const string GitHubApiVersion = "2022-11-28";
    private const string GitHubAcceptMediaType = "application/vnd.github+json";

    internal static HttpClient CreateDefaultHttpClient(TimeSpan timeout)
    {
        var client = CreateHttpClient(timeout);
        ApplyDefaultHeaders(client.DefaultRequestHeaders);
        return client;
    }

    internal static HttpClient CreateReleaseDownloadHttpClient(TimeSpan timeout)
    {
        var client = CreateHttpClient(timeout);
        ApplyReleaseDownloadHeaders(client.DefaultRequestHeaders);
        return client;
    }

    internal static HttpClientHandler CreateDefaultHttpClientHandler()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
        };
        if (ShouldUseDefaultProxyCredentials())
            handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;

        return handler;
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient(CreateDefaultHttpClientHandler())
        {
            Timeout = NormalizeRequestTimeout(timeout),
        };
        return client;
    }

    internal static RequestCancellationScope CreateRequestCancellationScope(
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => new(timeout, cancellationToken);

    internal sealed class RequestCancellationScope : IDisposable
    {
        private readonly OperationTimeoutScope scope;

        internal RequestCancellationScope(TimeSpan timeout, CancellationToken cancellationToken)
        {
            scope = OperationTimeoutScope.Create(
                OperationTimeoutCategories.GitHubApiRequest,
                NormalizeRequestTimeout(timeout),
                cancellationToken);
        }

        internal CancellationToken Token => scope.Token;

        internal bool IsTimeoutCancellationRequested => scope.IsTimeoutCancellationRequested;

        public void Dispose() => scope.Dispose();
    }

    internal static void ApplyDefaultHeaders(HttpRequestHeaders headers)
    {
        ApplyReleaseDownloadHeaders(headers);

        var hasGitHubAccept = false;
        foreach (var accept in headers.Accept)
        {
            if (string.Equals(accept.MediaType, GitHubAcceptMediaType, StringComparison.OrdinalIgnoreCase))
            {
                hasGitHubAccept = true;
                break;
            }
        }

        if (!hasGitHubAccept)
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubAcceptMediaType));

        if (!headers.Contains(GitHubApiVersionHeader))
            headers.Add(GitHubApiVersionHeader, GitHubApiVersion);
    }

    internal static void ApplyReleaseDownloadHeaders(HttpRequestHeaders headers)
    {
        if (headers.UserAgent.Count == 0)
            headers.UserAgent.Add(new ProductInfoHeaderValue(new ProductHeaderValue("cdidx")));
    }

    internal static async Task EnsureSuccessStatusCodeWithBoundedDiagnosticsAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var errorBody = string.Empty;
        if (response.Content != null)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            errorBody = await GitHubIssueReporter.ReadBoundedApiErrorBodyAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        var detail = GitHubIssueReporter.BuildApiErrorDetail((int)response.StatusCode, errorBody);
        throw new HttpRequestException(
            $"GitHub release download failed for {operation}: {detail}",
            null,
            response.StatusCode);
    }

    internal static bool ShouldUseDefaultProxyCredentials()
    {
        var raw = CdidxEnvironment.GetProcessEnvironmentVariable(ProxyDefaultCredentialsEnvironmentVariable)?.Trim();
        return raw != null
            && (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase));
    }

    internal static string FormatProxyDefaultCredentialsStatus()
        => ShouldUseDefaultProxyCredentials() ? "enabled" : "disabled";

    internal static TimeSpan NormalizeRequestTimeout(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero)
            return MaxRequestTimeout;
        return timeout <= MaxRequestTimeout ? timeout : MaxRequestTimeout;
    }
}
