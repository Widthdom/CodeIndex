namespace CodeIndex.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    internal static readonly DateTimeOffset FixtureUtcNow = DateTimeOffset.UnixEpoch.AddDays(20_000);
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public ManualTimeProvider()
        : this(FixtureUtcNow)
    {
    }

    public ManualTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow.ToUniversalTime();
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long GetTimestamp() => _timestamp;

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow.ToUniversalTime();

    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "Elapsed time must be non-negative.");

        _utcNow += elapsed;
        _timestamp += elapsed.Ticks;
    }

    public void AdjustUtc(TimeSpan offset) => _utcNow += offset;
}
