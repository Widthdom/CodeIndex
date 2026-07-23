namespace CodeIndex.Tests;

public sealed class ProductionCliTheoryAttribute : TheoryAttribute
{
    public ProductionCliTheoryAttribute()
    {
#if !NET8_0
        Skip = ProductionCliTestTarget.SecondaryTargetSkipReason;
#else
        if (OperatingSystem.IsWindows())
            Skip = ProductionCliTestTarget.WindowsSkipReason;
#endif
    }
}
