using System.Runtime.InteropServices;
using Xunit.Sdk;

namespace CodeIndex.Tests;

public sealed class PublishedTrimmedCliFactAttribute : FactAttribute
{
    public PublishedTrimmedCliFactAttribute()
    {
#if !NET8_0
        Skip = "Published trimmed CLI smoke tests run only on net8.0; the production CLI targets net8.0 and focused in-process tests cover cross-target behavior.";
#else
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Skip = "macOS arm64 SDK/ILLink can crash before this test can exercise cdidx (#2586).";
#endif
    }
}
