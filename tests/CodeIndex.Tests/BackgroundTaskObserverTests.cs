using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

public class BackgroundTaskObserverTests
{
    [Fact]
    public async Task Run_ReportsFaultedBackgroundTasks_Issue3401()
    {
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = BackgroundTaskObserver.Run(
            () => throw new InvalidOperationException("failed token=secret /tmp/private/file.txt"),
            "cdidx-test",
            "faulting worker",
            message =>
            {
                reported.TrySetResult(message);
            });

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);
        var warning = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Warning: background task 'faulting worker' failed in cdidx-test", warning);
        Assert.Contains("invalid_operation: InvalidOperationException", warning);
        Assert.DoesNotContain("secret", warning);
        Assert.DoesNotContain("/tmp/private", warning);
    }

    [Fact]
    public async Task Run_DoesNotReportCanceledBackgroundTasks_Issue3401()
    {
        var messages = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = BackgroundTaskObserver.Run(
            token => Task.FromCanceled(token),
            "cdidx-test",
            "cancelled worker",
            cts.Token,
            messages.Add);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);

        Assert.Empty(messages);
    }

    [Fact]
    public async Task Run_UsesShutdownCancellationWhenScheduling_Issue3760()
    {
        var started = false;
        var messages = new List<string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var task = BackgroundTaskObserver.Run(
            _ =>
            {
                started = true;
                return Task.CompletedTask;
            },
            "cdidx-test",
            "shutdown worker",
            cts.Token,
            messages.Add);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);

        Assert.False(started);
        Assert.Empty(messages);
    }
}
