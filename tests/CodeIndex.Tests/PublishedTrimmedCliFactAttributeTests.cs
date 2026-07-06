using System.Runtime.InteropServices;

namespace CodeIndex.Tests;

public class PublishedTrimmedCliFactAttributeTests
{
    [Fact]
    public void Constructor_AppliesPublishedCliSmokeSkipPolicy()
    {
        var attribute = new PublishedTrimmedCliFactAttribute();

#if NET8_0
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Assert.Contains("macOS arm64", attribute.Skip);
        else
            Assert.Null(attribute.Skip);
#else
        Assert.Contains("net8.0", attribute.Skip);
#endif
    }
}
