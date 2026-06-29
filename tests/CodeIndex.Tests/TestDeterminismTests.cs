using Xunit.Sdk;

namespace CodeIndex.Tests;

public class TestDeterminismTests
{
    [Fact]
    public void CreateRandom_DefaultSeed_ReplaysSequence()
    {
        var first = TestDeterminism.CreateRandom();
        var second = TestDeterminism.CreateRandom();

        var firstValues = Enumerable.Range(0, 5).Select(_ => first.Next()).ToArray();
        var secondValues = Enumerable.Range(0, 5).Select(_ => second.Next()).ToArray();

        Assert.Equal(firstValues, secondValues);
    }

    [Fact]
    public void ManualTimeProvider_AdvanceMovesUtcAndMonotonicTimestampTogether()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2034, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var started = clock.GetTimestamp();

        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(new DateTimeOffset(2034, 1, 2, 3, 4, 8, TimeSpan.Zero), clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(3), clock.GetElapsedTime(started));
    }

    [Fact]
    public async Task WaitUntilAsync_PollsUntilConditionIsTrue()
    {
        var polls = 0;

        await TestDeterminism.WaitUntilAsync(
            () => Interlocked.Increment(ref polls) >= 3,
            "third poll",
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.True(polls >= 3);
    }

    [Fact]
    public async Task WaitUntilAsync_TimeoutIncludesDiagnostics()
    {
        var ex = await Assert.ThrowsAnyAsync<XunitException>(() =>
            TestDeterminism.WaitUntilAsync(
                () => false,
                "missing condition",
                timeout: TimeSpan.FromMilliseconds(5),
                pollInterval: TimeSpan.FromMilliseconds(1),
                getDiagnostics: () => "observed=0"));

        Assert.Contains("missing condition", ex.Message, StringComparison.Ordinal);
        Assert.Contains("observed=0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitUntilOrTimeoutAsync_ReturnsFalseOnTimeout()
    {
        var observed = await TestDeterminism.WaitUntilOrTimeoutAsync(
            () => false,
            TimeSpan.FromMilliseconds(5),
            pollInterval: TimeSpan.FromMilliseconds(1));

        Assert.False(observed);
    }

    [Fact]
    public async Task AssertTaskRemainsBlockedAsync_ReturnsWhenTaskIsStillPending()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await TestDeterminism.AssertTaskRemainsBlockedAsync(
            blocker.Task,
            TimeSpan.FromMilliseconds(5));

        blocker.SetResult();
    }

    [Fact]
    public async Task RunConcurrentlyAsync_ReturnsEachWorkerResult()
    {
        var started = 0;

        var results = await TestDeterminism.RunConcurrentlyAsync(
            Enumerable.Range(0, 4).Select<int, Func<int>>(worker => () =>
            {
                Interlocked.Increment(ref started);
                return worker;
            }));

        Assert.Equal(4, started);
        Assert.Equal([0, 1, 2, 3], results.Order());
    }
}
