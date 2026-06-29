using CodeIndex.Database;

namespace CodeIndex.Cli;

internal sealed class IndexWatchStartedJsonResult
{
    public string ApiVersion { get; init; } = JsonOutputContract.ApiVersion;
    public string Status { get; init; } = string.Empty;
    public string? Phase { get; init; }
    public string? ProjectRoot { get; init; }
    public string? Db { get; init; }
    public int? DebounceMs { get; init; }
    public int? WatchPendingPathLimit { get; init; }
    public IndexWatchContractJsonResult? WatchContract { get; init; }
}

internal sealed class IndexWatchContractJsonResult
{
    public string Debounce { get; init; } = string.Empty;
    public int DebounceMs { get; init; }
    public int MaxDebounceMs { get; init; }
    public int PollIntervalMs { get; init; }
    public int WatchPendingPathLimit { get; init; }
    public string PathComparison { get; init; } = string.Empty;
    public string ChangeCoalescing { get; init; } = string.Empty;
    public string RenameEvents { get; init; } = string.Empty;
    public string OverflowRecovery { get; init; } = string.Empty;
    public string WatcherErrorRecovery { get; init; } = string.Empty;
    public string Cancellation { get; init; } = string.Empty;
    public string SubRunOutput { get; init; } = string.Empty;
    public string McpWatchMode { get; init; } = string.Empty;
}
