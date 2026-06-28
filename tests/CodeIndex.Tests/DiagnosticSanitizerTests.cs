using System.Text.RegularExpressions;
using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

public class DiagnosticSanitizerTests
{
    [Fact]
    public void ForMessage_RedactsPathsAndCollapsesWhitespace()
    {
        var sanitized = DiagnosticSanitizer.ForMessage("failed\nat /tmp/codeindex/plugins/bad.dll\twith   details");

        Assert.Equal("failed at <path> with details", sanitized);
    }

    [Theory]
    [InlineData(@"failed at C:\Users\me\.config\cdidx\hook.dll, with details", "failed at <path>, with details")]
    [InlineData(@"failed at C:/Users/me/.config/cdidx/hook.dll; with details", "failed at <path>; with details")]
    public void ForMessage_RedactsWindowsPaths(string message, string expected)
    {
        var sanitized = DiagnosticSanitizer.ForMessage(message);

        Assert.Equal(expected, sanitized);
    }

    [Theory]
    [InlineData("Access to the path '/Users/Jane Doe/cdidx audit.log' is denied.", "Access to the path '<path>' is denied.")]
    [InlineData("Access to the path \"C:\\Users\\Jane Doe\\cdidx audit.log\" is denied.", "Access to the path \"<path>\" is denied.")]
    public void ForMessage_RedactsQuotedPathsWithSpaces_Issue4123(string message, string expected)
    {
        var sanitized = DiagnosticSanitizer.ForMessage(message);

        Assert.Equal(expected, sanitized);
        Assert.DoesNotContain("Jane", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Doe", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("audit.log", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void ForMessage_RedactsSensitiveAssignments_Issue4069()
    {
        var sanitized = DiagnosticSanitizer.ForMessage("failed command --token=abc123 at /tmp/codeindex/plugins/bad.dll");

        Assert.Equal("failed command --token=<redacted> at <path>", sanitized);
        Assert.DoesNotContain("abc123", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("/tmp/codeindex", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void ForMessage_RedactionTimeout_ReturnsFallbackMessage()
    {
        var timeout = new RegexMatchTimeoutException(
            "load failed at /tmp/codeindex/plugins/bad.dll",
            "path",
            TimeSpan.FromMilliseconds(50));

        var sanitized = DiagnosticSanitizer.ForMessage(
            "load failed at /tmp/codeindex/plugins/bad.dll",
            _ => throw timeout);

        Assert.Equal(RegexTimeoutPolicy.DiagnosticSanitizerTimeoutFallback, sanitized);
        Assert.Equal(DiagnosticSanitizer.RegexTimeoutFallbackMessage, sanitized);
    }
}
