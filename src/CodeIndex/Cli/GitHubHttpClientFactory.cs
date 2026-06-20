using System.Net;
using System.Net.Http.Headers;

namespace CodeIndex.Cli;

internal static class GitHubHttpClientFactory
{
    internal const string ProxyDefaultCredentialsEnvironmentVariable = "CDIDX_GITHUB_PROXY_USE_DEFAULT_CREDENTIALS";
    private const string GitHubApiVersionHeader = "X-GitHub-Api-Version";
    private const string GitHubApiVersion = "2022-11-28";
    private const string GitHubAcceptMediaType = "application/vnd.github+json";

    internal static HttpClient CreateDefaultHttpClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = HttpClient.DefaultProxy,
        };
        if (ShouldUseDefaultProxyCredentials())
            handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;

        var client = new HttpClient(handler)
        {
            Timeout = timeout,
        };
        ApplyDefaultHeaders(client.DefaultRequestHeaders);
        return client;
    }

    internal static void ApplyDefaultHeaders(HttpRequestHeaders headers)
    {
        if (headers.UserAgent.Count == 0)
            headers.UserAgent.Add(new ProductInfoHeaderValue(new ProductHeaderValue("cdidx")));

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

    internal static bool ShouldUseDefaultProxyCredentials()
    {
        var raw = Environment.GetEnvironmentVariable(ProxyDefaultCredentialsEnvironmentVariable)?.Trim();
        return raw != null
            && (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase));
    }
}
