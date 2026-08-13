namespace CodeIndex.Tests;

public sealed class ManualPerformanceFactAttribute : FactAttribute
{
    internal const string EnvironmentVariable = "CDIDX_RUN_MANUAL_PERFORMANCE_TESTS";
    internal const string OptInSkipReason =
        "Manual performance test. Set CDIDX_RUN_MANUAL_PERFORMANCE_TESTS=1 and select this test with --filter to run it.";

    public ManualPerformanceFactAttribute()
    {
#if NET8_0
        if (!IsEnabled(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = OptInSkipReason;
#else
        Skip = ProductionRuntimeTestTarget.SecondaryTargetSkipReason;
#endif
    }

    internal static bool IsEnabled(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal);
}
