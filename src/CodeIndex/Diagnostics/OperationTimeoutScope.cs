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

internal readonly record struct OperationTimeoutBudget
{
    private OperationTimeoutBudget(TimeSpan? duration)
    {
        Duration = duration;
    }

    internal TimeSpan? Duration { get; }

    internal bool HasTimeout => Duration.HasValue;

    internal static OperationTimeoutBudget None { get; } = new(null);

    internal static OperationTimeoutBudget After(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout budget must be positive.");

        return new OperationTimeoutBudget(timeout);
    }

    internal static OperationTimeoutBudget FromTimeout(TimeSpan timeout)
        => IsDisabledTimeout(timeout) ? None : After(timeout);

    internal static OperationTimeoutBudget FromTimeoutOrDefault(TimeSpan timeout, TimeSpan defaultTimeout)
        => IsDisabledTimeout(timeout) ? After(defaultTimeout) : After(timeout);

    internal OperationTimeoutBudget Clamp(TimeSpan maxTimeout)
    {
        if (maxTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxTimeout), maxTimeout, "Maximum timeout budget must be positive.");

        return Duration is { } duration && duration > maxTimeout
            ? new OperationTimeoutBudget(maxTimeout)
            : this;
    }

    private static bool IsDisabledTimeout(TimeSpan timeout)
        => timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero;
}

internal sealed class OperationTimeoutScope : IDisposable
{
    private readonly CancellationTokenSource? _timeoutCts;
    private readonly CancellationTokenSource _linkedCts;

    private OperationTimeoutScope(string category, OperationTimeoutBudget budget, CancellationToken cancellationToken)
    {
        Category = category;
        Budget = budget;
        if (budget.Duration is { } timeout)
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

    internal OperationTimeoutBudget Budget { get; }

    internal TimeSpan Timeout => Budget.Duration ?? System.Threading.Timeout.InfiniteTimeSpan;

    internal CancellationToken Token => _linkedCts.Token;

    internal bool IsTimeoutCancellationRequested => _timeoutCts?.IsCancellationRequested == true;

    internal static OperationTimeoutScope Create(
        string category,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Timeout category must be non-empty.", nameof(category));

        return new OperationTimeoutScope(category, OperationTimeoutBudget.FromTimeout(timeout), cancellationToken);
    }

    internal static OperationTimeoutScope Create(
        string category,
        OperationTimeoutBudget budget,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Timeout category must be non-empty.", nameof(category));

        return new OperationTimeoutScope(category, budget, cancellationToken);
    }

    public void Dispose()
    {
        _linkedCts.Dispose();
        _timeoutCts?.Dispose();
    }
}
