namespace CodeIndex.Tests;

public sealed class ProductionCliFactAttribute : FactAttribute
{
    public ProductionCliFactAttribute()
    {
#if !NET8_0
        Skip = ProductionCliTestTarget.SecondaryTargetSkipReason;
#else
        if (OperatingSystem.IsWindows())
            Skip = ProductionCliTestTarget.WindowsSkipReason;
#endif
    }
}
