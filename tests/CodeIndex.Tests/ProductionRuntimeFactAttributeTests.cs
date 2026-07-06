namespace CodeIndex.Tests;

public class ProductionRuntimeFactAttributeTests
{
    [Fact]
    public void Constructor_AppliesProductionRuntimeTargetSkipPolicy()
    {
        var fact = new ProductionRuntimeFactAttribute();

#if NET8_0
        Assert.Null(fact.Skip);
#else
        Assert.Equal(ProductionRuntimeTestTarget.SecondaryTargetSkipReason, fact.Skip);
#endif
    }
}
