namespace CodeIndex.Tests;

public sealed class ProductionRuntimeTheoryAttribute : TheoryAttribute
{
    public ProductionRuntimeTheoryAttribute()
    {
#if !NET8_0
        Skip = ProductionRuntimeTestTarget.SecondaryTargetSkipReason;
#endif
    }
}
