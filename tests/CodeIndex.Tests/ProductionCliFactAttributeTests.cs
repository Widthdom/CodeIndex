namespace CodeIndex.Tests;

public class ProductionCliFactAttributeTests
{
    [Fact]
    public void Constructors_ApplyProductionCliTargetSkipPolicy()
    {
        var fact = new ProductionCliFactAttribute();
        var theory = new ProductionCliTheoryAttribute();

#if NET8_0
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ProductionCliTestTarget.WindowsSkipReason, fact.Skip);
            Assert.Equal(ProductionCliTestTarget.WindowsSkipReason, theory.Skip);
        }
        else
        {
            Assert.Null(fact.Skip);
            Assert.Null(theory.Skip);
        }
#else
        Assert.Equal(ProductionCliTestTarget.SecondaryTargetSkipReason, fact.Skip);
        Assert.Equal(ProductionCliTestTarget.SecondaryTargetSkipReason, theory.Skip);
#endif
    }
}
