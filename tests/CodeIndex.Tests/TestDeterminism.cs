namespace CodeIndex.Tests;

internal static class TestDeterminism
{
    internal const int DefaultRandomSeed = 0x5EED_4164;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(10);
    internal static readonly TimeSpan BlockedObservationWindow = TimeSpan.FromMilliseconds(100);

    internal static Random CreateRandom(int seed = DefaultRandomSeed)
        => new(seed);

    internal static async Task WaitUntilAsync(
        Func<bool> condition,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        Func<string>? getDiagnostics = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A wait description is required.", nameof(description));

        var effectiveTimeout = timeout ?? DefaultTimeout;
        var effectivePollInterval = pollInterval ?? DefaultPollInterval;
        if (effectiveTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), effectiveTimeout, "Timeout must be non-negative.");
        if (effectivePollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), effectivePollInterval, "Poll interval must be positive.");

        var started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < effectiveTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
                return;

            await Task.Delay(effectivePollInterval, cancellationToken);
        }

        if (condition())
            return;

        var diagnostics = getDiagnostics?.Invoke();
        Assert.Fail(string.IsNullOrWhiteSpace(diagnostics)
            ? $"Timed out waiting for {description}."
            : $"Timed out waiting for {description}. {diagnostics}");
    }

    internal static async Task<bool> WaitUntilOrTimeoutAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be non-negative.");

        var effectivePollInterval = pollInterval ?? DefaultPollInterval;
        if (effectivePollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), effectivePollInterval, "Poll interval must be positive.");

        var started = TimeProvider.System.GetTimestamp();
        while (TimeProvider.System.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
                return true;

            await Task.Delay(effectivePollInterval, cancellationToken);
        }

        return condition();
    }

    internal static async Task AssertTaskRemainsBlockedAsync(
        Task task,
        TimeSpan? observationWindow = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var completed = await Task.WhenAny(
            task,
            Task.Delay(observationWindow ?? BlockedObservationWindow));

        Assert.NotSame(task, completed);
    }

    internal static Task RunConcurrentlyAsync(params Action[] workers)
        => RunConcurrentlyAsync((IEnumerable<Action>)workers);

    internal static async Task RunConcurrentlyAsync(
        IEnumerable<Action> workers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workers);

        await RunConcurrentlyAsync(
            workers.Select<Action, Func<object?>>(
                worker => () =>
                {
                    worker();
                    return null;
                }),
            cancellationToken);
    }

    internal static async Task<T[]> RunConcurrentlyAsync<T>(
        IEnumerable<Func<T>> workers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workers);

        var workerArray = workers.ToArray();
        if (workerArray.Length == 0)
            return [];

        using var start = new ManualResetEventSlim(false);
        using var ready = new ManualResetEventSlim(false);
        var readyCount = 0;
        var tasks = workerArray
            .Select(worker => Task.Run(
                () =>
                {
                    if (Interlocked.Increment(ref readyCount) == workerArray.Length)
                        ready.Set();

                    start.Wait(cancellationToken);
                    return worker();
                },
                cancellationToken))
            .ToArray();

        if (!ready.Wait(DefaultTimeout, cancellationToken))
        {
            start.Set();
            await Task.WhenAll(tasks);
            Assert.Fail($"Timed out waiting for {workerArray.Length} workers to reach the start gate. Ready workers: {Volatile.Read(ref readyCount)}.");
        }

        start.Set();
        return await Task.WhenAll(tasks);
    }
}
