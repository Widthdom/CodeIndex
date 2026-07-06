namespace CodeIndex.Tests;

public sealed class ProductionCliFactAttribute : FactAttribute
{
    public ProductionCliFactAttribute()
    {
#if !NET8_0
        Skip = ProductionCliTestTarget.SecondaryTargetSkipReason;
#endif
    }
}
