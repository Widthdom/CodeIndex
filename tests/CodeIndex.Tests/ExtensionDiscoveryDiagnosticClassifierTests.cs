using CodeIndex.Indexer.Extensibility;

namespace CodeIndex.Tests;

public class ExtensionDiscoveryDiagnosticClassifierTests
{
    [Theory]
    [InlineData("plugin", typeof(PathTooLongException), "plugin_directory_path_too_long")]
    [InlineData("pattern", typeof(DirectoryNotFoundException), "pattern_directory_directory_missing")]
    [InlineData("hook", typeof(UnauthorizedAccessException), "hook_directory_permission_denied")]
    [InlineData("plugin", typeof(ArgumentException), "plugin_directory_path_invalid")]
    [InlineData("pattern", typeof(NotSupportedException), "pattern_directory_path_invalid")]
    [InlineData("hook", typeof(IOException), "hook_directory_enumeration_failed")]
    public void ClassifyDirectoryEnumerationFailure_UsesSharedBoundedTaxonomy(
        string prefix,
        Type exceptionType,
        string expectedCategory)
    {
        var exception = (Exception)Activator.CreateInstance(
            exceptionType,
            new string('x', 1024))!;

        var diagnostic = ExtensionDiscoveryDiagnosticClassifier.ClassifyDirectoryEnumerationFailure(
            prefix,
            "Test directory",
            exception);

        Assert.Equal(expectedCategory, diagnostic.Category);
        Assert.StartsWith("Test directory skipped:", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(exceptionType.Name, diagnostic.Message, StringComparison.Ordinal);
        Assert.True(diagnostic.Message.Length < 240, diagnostic.Message);
    }
}
