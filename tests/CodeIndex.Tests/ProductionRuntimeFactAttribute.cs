namespace CodeIndex.Tests;

public sealed class ProductionRuntimeFactAttribute : FactAttribute
{
    public ProductionRuntimeFactAttribute()
    {
#if !NET8_0
        Skip = ProductionRuntimeTestTarget.SecondaryTargetSkipReason;
#endif
    }
}
