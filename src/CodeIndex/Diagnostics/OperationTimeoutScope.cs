namespace CodeIndex.Diagnostics;

internal static class OperationTimeoutCategories
{
    internal const string GitHubApiRequest = "github_api_request";
    internal const string UpgradeDownload = "upgrade_download";
    internal const string McpRequest = "mcp_request";
    internal const string McpClientRequest = "mcp_client_request";
    internal const string HttpResponseWrite = "http_response_write";
    internal const string SseWrite = "sse_write";
}

internal sealed class OperationTimeoutScope : IDisposable
{
    private readonly CancellationTokenSource? _timeoutCts;
    private readonly CancellationTokenSource _linkedCts;

    private OperationTimeoutScope(string category, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Category = category;
        Timeout = timeout;
        if (timeout > TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            _timeoutCts = new CancellationTokenSource(timeout);
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeoutCts.Token);
        }
        else
        {
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
    }

    internal string Category { get; }

    internal TimeSpan Timeout { get; }

    internal CancellationToken Token => _linkedCts.Token;

    internal bool IsTimeoutCancellationRequested => _timeoutCts?.IsCancellationRequested == true;

    internal static OperationTimeoutScope Create(
        string category,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Timeout category must be non-empty.", nameof(category));

        return new OperationTimeoutScope(category, timeout, cancellationToken);
    }

    public void Dispose()
    {
        _linkedCts.Dispose();
        _timeoutCts?.Dispose();
    }
}
