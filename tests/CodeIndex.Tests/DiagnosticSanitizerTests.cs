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

        Assert.Equal(DiagnosticSanitizer.RegexTimeoutFallbackMessage, sanitized);
    }
}
