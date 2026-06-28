using CodeIndex.PackageNormalize;

namespace CodeIndex.Tests;

public class PackageNormalizeDiagnosticsTests
{
    [Fact]
    public void FormatException_RedactsExceptionMessages_Issue4124()
    {
        var exception = new ArgumentException(
            "rewrite failed at /tmp/private/package.nupkg with --token=ghp_abcdefghijklmnopqrstuvwxyz and password=hunter2");

        var message = PackageNormalizeDiagnostics.FormatException("/tmp/package.nupkg", exception);

        Assert.Contains("<path>", message, StringComparison.Ordinal);
        Assert.Contains("--token=<redacted>", message, StringComparison.Ordinal);
        Assert.Contains("password=<redacted>", message, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/private", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }
}
