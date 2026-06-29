using System.Net;
using System.Net.Http.Headers;
using CodeIndex.Diagnostics;

namespace CodeIndex.Cli;

internal static class GitHubHttpClientFactory
{
    internal const string ProxyDefaultCredentialsEnvironmentVariable = "CDIDX_GITHUB_PROXY_USE_DEFAULT_CREDENTIALS";
    internal const int MaxRetryAttempts = 3;
    internal static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
    ];
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

    internal static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);

        for (var attempt = 1; ; attempt++)
        {
            using var request = requestFactory();
            var canRetry = IsRetryableRequestMethod(request.Method);
            try
            {
                var response = await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
                if (!canRetry || attempt >= MaxRetryAttempts || !IsTransientRetryStatusCode(response.StatusCode))
                    return response;

                response.Dispose();
            }
            catch (HttpRequestException) when (canRetry
                && attempt < MaxRetryAttempts
                && !cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsTransientRetryStatusCode(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout
            || ((int)statusCode >= 500 && (int)statusCode <= 599);

    internal static bool IsRetryableRequestMethod(HttpMethod method)
        => method == HttpMethod.Get || method == HttpMethod.Head;

    private static TimeSpan GetRetryDelay(int completedAttempt)
        => completedAttempt <= 0
            ? TimeSpan.Zero
            : completedAttempt <= RetryDelays.Length
                ? RetryDelays[completedAttempt - 1]
                : RetryDelays[^1];

    internal sealed class RequestCancellationScope : IDisposable
    {
        private readonly OperationTimeoutScope scope;

        internal RequestCancellationScope(TimeSpan timeout, CancellationToken cancellationToken)
        {
            scope = OperationTimeoutScope.Create(
                OperationTimeoutCategories.GitHubApiRequest,
                NormalizeRequestTimeoutBudget(timeout),
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
        => NormalizeRequestTimeoutBudget(timeout).Duration ?? MaxRequestTimeout;

    internal static OperationTimeoutBudget NormalizeRequestTimeoutBudget(TimeSpan timeout)
        => OperationTimeoutBudget.FromTimeoutOrDefault(timeout, MaxRequestTimeout).Clamp(MaxRequestTimeout);
}
