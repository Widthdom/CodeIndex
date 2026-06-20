namespace CodeIndex.Indexer.Extensibility;

internal static class ExtensionDiscoveryDiagnosticClassifier
{
    internal static bool IsDiscoveryException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;

    internal static ExtensionDiscoveryDiagnostic ClassifyDirectoryEnumerationFailure(
        string categoryPrefix,
        string surfaceLabel,
        Exception ex)
    {
        var reason = ex switch
        {
            PathTooLongException => "path is too long",
            DirectoryNotFoundException => "directory does not exist",
            UnauthorizedAccessException => "permission denied",
            ArgumentException or NotSupportedException => "path is invalid",
            _ => "could not enumerate directory",
        };
        var categorySuffix = ex switch
        {
            PathTooLongException => "path_too_long",
            DirectoryNotFoundException => "directory_missing",
            UnauthorizedAccessException => "permission_denied",
            ArgumentException or NotSupportedException => "path_invalid",
            _ => "enumeration_failed",
        };
        var category = $"{categoryPrefix}_directory_{categorySuffix}";
        var exceptionCategory = SafeDiagnosticFormatter.FormatExceptionCategory(category, ex);
        return new ExtensionDiscoveryDiagnostic(
            category,
            $"{surfaceLabel} skipped: {reason} ({exceptionCategory})");
    }
}

internal sealed record ExtensionDiscoveryDiagnostic(string Category, string Message);
