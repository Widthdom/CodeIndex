namespace CodeIndex.Tests;

public class ManualPerformanceFactAttributeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("01", false)]
    [InlineData("1", true)]
    public void IsEnabled_RequiresExactExplicitOptIn(string? value, bool expected)
        => Assert.Equal(expected, ManualPerformanceFactAttribute.IsEnabled(value));

    [Fact]
    public void Constructor_AppliesRuntimeAndExplicitOptInPolicy()
    {
        var attribute = new ManualPerformanceFactAttribute();

#if NET8_0
        var enabled = ManualPerformanceFactAttribute.IsEnabled(
            Environment.GetEnvironmentVariable(ManualPerformanceFactAttribute.EnvironmentVariable));
        Assert.Equal(enabled ? null : ManualPerformanceFactAttribute.OptInSkipReason, attribute.Skip);
#else
        Assert.Equal(ProductionRuntimeTestTarget.SecondaryTargetSkipReason, attribute.Skip);
#endif
    }
}
