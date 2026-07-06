namespace CodeIndex.Tests;

public class ProductionRuntimeFactAttributeTests
{
    [Fact]
    public void Constructors_ApplyProductionRuntimeTargetSkipPolicy()
    {
        var fact = new ProductionRuntimeFactAttribute();
        var theory = new ProductionRuntimeTheoryAttribute();

#if NET8_0
        Assert.Null(fact.Skip);
        Assert.Null(theory.Skip);
#else
        Assert.Equal(ProductionRuntimeTestTarget.SecondaryTargetSkipReason, fact.Skip);
        Assert.Equal(ProductionRuntimeTestTarget.SecondaryTargetSkipReason, theory.Skip);
#endif
    }
}
