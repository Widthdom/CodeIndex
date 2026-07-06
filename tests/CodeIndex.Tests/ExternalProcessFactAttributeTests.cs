namespace CodeIndex.Tests;

public class ExternalProcessFactAttributeTests
{
    [Fact]
    public void Constructors_ApplyExternalProcessTargetSkipPolicy()
    {
        var fact = new ExternalProcessFactAttribute();
        var theory = new ExternalProcessTheoryAttribute();

#if NET8_0
        Assert.Null(fact.Skip);
        Assert.Null(theory.Skip);
#else
        Assert.Equal(ExternalProcessTestTarget.SecondaryTargetSkipReason, fact.Skip);
        Assert.Equal(ExternalProcessTestTarget.SecondaryTargetSkipReason, theory.Skip);
#endif
    }
}
