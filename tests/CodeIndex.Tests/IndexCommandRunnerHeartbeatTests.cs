using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class IndexCommandRunnerHeartbeatTests
{
    [Fact]
    public async Task StartObservedJsonPhaseHeartbeat_ReportsFailures_Issue3705()
    {
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = IndexCommandRunner.StartObservedJsonPhaseHeartbeat(
            enabled: true,
            component: "cdidx-test",
            phase: "testing",
            messageWriter: _ => { },
            detailProvider: () => throw new InvalidOperationException("failed token=secret /tmp/private/file.txt"),
            interval: TimeSpan.Zero,
            warningWriter: message => reported.TrySetResult(message));

        Assert.NotNull(heartbeat);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await heartbeat.Value.Task);
            var warning = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Contains("Warning: background task 'testing heartbeat' failed in cdidx-test", warning);
            Assert.Contains("invalid_operation: InvalidOperationException", warning);
            Assert.DoesNotContain("secret", warning);
            Assert.DoesNotContain("/tmp/private", warning);
        }
        finally
        {
            heartbeat.Value.Cts.Cancel();
            heartbeat.Value.Cts.Dispose();
        }
    }

    [Fact]
    public async Task StartObservedJsonPhaseHeartbeat_StopsQuietlyOnExpectedCancellation_Issue3705()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warnings = new List<string>();
        var heartbeat = IndexCommandRunner.StartObservedJsonPhaseHeartbeat(
            enabled: true,
            component: "cdidx-test",
            phase: "testing",
            messageWriter: _ => { },
            detailProvider: () =>
            {
                started.TrySetResult();
                return null;
            },
            interval: TimeSpan.FromMilliseconds(1),
            warningWriter: warnings.Add);

        Assert.NotNull(heartbeat);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        heartbeat.Value.Cts.Cancel();
        await heartbeat.Value.Task.WaitAsync(TimeSpan.FromSeconds(5));
        heartbeat.Value.Cts.Dispose();

        Assert.Empty(warnings);
    }
}
