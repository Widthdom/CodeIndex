using System.Net;

namespace CodeIndex.Cli;

internal static class GitHubHttpClientFactory
{
    internal const string ProxyDefaultCredentialsEnvironmentVariable = "CDIDX_GITHUB_PROXY_USE_DEFAULT_CREDENTIALS";

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
            DefaultRequestHeaders =
            {
                { "User-Agent", "cdidx" },
                { "Accept", "application/vnd.github+json" },
                { "X-GitHub-Api-Version", "2022-11-28" },
            },
        };
        return client;
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
