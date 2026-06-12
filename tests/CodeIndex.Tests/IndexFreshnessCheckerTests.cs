using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class IndexFreshnessCheckerTests
{
    [Fact]
    public void FormatScanFailureSample_ClassifiesIoMessage_Issue3471()
    {
        const string secret = "0123456789abcdef0123456789abcdef";
        var rawPath = "/Users/example/private/project/secret.cs";
        var exception = new IOException($"Could not read {rawPath} token={secret}");

        var sample = IndexFreshnessChecker.FormatScanFailureSample("src/App.cs", exception);

        Assert.Equal("src/App.cs: io-error", sample);
        Assert.DoesNotContain(rawPath, sample);
        Assert.DoesNotContain(secret, sample);
    }

    [Fact]
    public void FormatScanFailureSample_ClassifiesAccessDenied_Issue3471()
    {
        var exception = new UnauthorizedAccessException("/Users/example/private/project/secret.cs");

        var sample = IndexFreshnessChecker.FormatScanFailureSample("src/Secret.cs", exception);

        Assert.Equal("src/Secret.cs: access-denied", sample);
    }

    [Fact]
    public void FormatScanFailureSample_ClassifiesProbeFailure_Issue3471()
    {
        var exception = new InvalidOperationException("probe failed for /Users/example/private/project/secret.cs");

        var sample = IndexFreshnessChecker.FormatScanFailureSample("src/Broken.cs", exception);

        Assert.Equal("src/Broken.cs: probe-failed", sample);
    }
}
