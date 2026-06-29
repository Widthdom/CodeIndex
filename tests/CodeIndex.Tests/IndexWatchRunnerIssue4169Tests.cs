using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public class IndexWatchRunnerIssue4169Tests
{
    [Fact]
    public void FileChangeBatcher_DuplicatePathRefreshesDebounceWithoutDuplicatingBatch_Issue4169()
    {
        var timeProvider = new WatchManualTimeProvider();
        var batcher = new FileChangeBatcher(TimeSpan.FromMilliseconds(500), timeProvider);
        batcher.Add("/repo/a.py");

        timeProvider.Advance(TimeSpan.FromMilliseconds(400));
        batcher.Add("/repo/A.py");

        timeProvider.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(batcher.TryDrain(out _, out _, out _));

        timeProvider.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(batcher.TryDrain(out var batch, out var rescan, out _));
        Assert.False(rescan);
        Assert.Equal(["/repo/a.py"], batch);
    }

    [Fact]
    public void FileChangeBatcher_OverflowWaitsForQuietAfterSubsequentEvents_Issue4169()
    {
        var timeProvider = new WatchManualTimeProvider();
        var batcher = new FileChangeBatcher(
            TimeSpan.FromMilliseconds(100),
            timeProvider,
            maxPendingPaths: 1);

        batcher.Add("/repo/a.py");
        batcher.Add("/repo/b.py");

        timeProvider.Advance(TimeSpan.FromMilliseconds(90));
        batcher.Add("/repo/c.py");

        timeProvider.Advance(TimeSpan.FromMilliseconds(50));
        Assert.False(batcher.TryDrain(out _, out _, out _));

        timeProvider.Advance(TimeSpan.FromMilliseconds(60));
        Assert.True(batcher.TryDrain(out var batch, out var rescan, out var reason));
        Assert.True(rescan);
        Assert.Empty(batch);
        Assert.Contains("pending path limit exceeded", reason);
    }

    [Fact]
    public void BuildWatchContract_ReportsStableWatchSemantics_Issue4169()
    {
        var contract = IndexWatchRunner.BuildWatchContractForTesting(
            TimeSpan.FromMilliseconds(250),
            maxPendingPaths: 99,
            ignoreCase: false);

        Assert.Equal("quiet_window", contract.Debounce);
        Assert.Equal(250, contract.DebounceMs);
        Assert.Equal(IndexWatchRunner.MaxDebounceMs, contract.MaxDebounceMs);
        Assert.Equal(50, contract.PollIntervalMs);
        Assert.Equal(99, contract.WatchPendingPathLimit);
        Assert.Equal("ordinal", contract.PathComparison);
        Assert.Equal("distinct_paths_refresh_debounce", contract.ChangeCoalescing);
        Assert.Equal("old_and_new_paths", contract.RenameEvents);
        Assert.Equal("full_rescan_after_debounce", contract.OverflowRecovery);
        Assert.Equal("full_rescan_after_debounce", contract.WatcherErrorRecovery);
        Assert.Equal("emit_stopped_after_current_poll_or_sub_run", contract.Cancellation);
        Assert.Equal("json_quiet_sub_runs", contract.SubRunOutput);
        Assert.Equal("unsupported", contract.McpWatchMode);
    }

    [Fact]
    public void IndexWatchStartedJsonResult_SerializesWatchContract_Issue4169()
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        var payload = new IndexWatchStartedJsonResult
        {
            Status = "watching",
            Phase = "initial_scan",
            ProjectRoot = "[redacted]",
            Db = "[redacted]",
            DebounceMs = 50,
            WatchPendingPathLimit = 123,
            WatchContract = IndexWatchRunner.BuildWatchContractForTesting(
                TimeSpan.FromMilliseconds(50),
                maxPendingPaths: 123,
                ignoreCase: true),
        };

        var json = JsonSerializer.Serialize(
            payload,
            CliJsonSerializerContextFactory.Create(jsonOptions).IndexWatchStartedJsonResult);

        using var watchStarted = JsonDocument.Parse(json);
        Assert.Equal(JsonOutputContract.ApiVersion, watchStarted.RootElement.GetProperty("api_version").GetString());
        Assert.Equal("watching", watchStarted.RootElement.GetProperty("status").GetString());
        Assert.Equal("initial_scan", watchStarted.RootElement.GetProperty("phase").GetString());
        Assert.Equal("[redacted]", watchStarted.RootElement.GetProperty("project_root").GetString());
        Assert.Equal("[redacted]", watchStarted.RootElement.GetProperty("db").GetString());
        Assert.Equal(50, watchStarted.RootElement.GetProperty("debounce_ms").GetInt32());
        Assert.Equal(123, watchStarted.RootElement.GetProperty("watch_pending_path_limit").GetInt32());

        var contract = watchStarted.RootElement.GetProperty("watch_contract");
        Assert.Equal("quiet_window", contract.GetProperty("debounce").GetString());
        Assert.Equal(50, contract.GetProperty("debounce_ms").GetInt32());
        Assert.Equal(IndexWatchRunner.MaxDebounceMs, contract.GetProperty("max_debounce_ms").GetInt32());
        Assert.Equal(50, contract.GetProperty("poll_interval_ms").GetInt32());
        Assert.Equal(123, contract.GetProperty("watch_pending_path_limit").GetInt32());
        Assert.Equal("ordinal_ignore_case", contract.GetProperty("path_comparison").GetString());
        Assert.Equal("distinct_paths_refresh_debounce", contract.GetProperty("change_coalescing").GetString());
        Assert.Equal("old_and_new_paths", contract.GetProperty("rename_events").GetString());
        Assert.Equal("full_rescan_after_debounce", contract.GetProperty("overflow_recovery").GetString());
        Assert.Equal("full_rescan_after_debounce", contract.GetProperty("watcher_error_recovery").GetString());
        Assert.Equal("emit_stopped_after_current_poll_or_sub_run", contract.GetProperty("cancellation").GetString());
        Assert.Equal("json_quiet_sub_runs", contract.GetProperty("sub_run_output").GetString());
        Assert.Equal("unsupported", contract.GetProperty("mcp_watch_mode").GetString());
    }

    private sealed class WatchManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time must be non-negative.");

            utcNow += elapsed;
            timestamp += elapsed.Ticks;
        }
    }
}
