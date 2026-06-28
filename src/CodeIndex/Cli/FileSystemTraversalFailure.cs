namespace CodeIndex.Cli;

internal static class FileSystemTraversalFailure
{
    internal static bool IsExpected(Exception ex)
        => CodeIndex.FileSystemTraversalPolicy.IsExpectedTraversalException(ex);

    internal static string DescribeReason(Exception ex)
        => CodeIndex.FileSystemTraversalPolicy.DescribeFailureReason(ex);

    internal static string ExceptionTypeName(Exception ex)
        => CodeIndex.FileSystemTraversalPolicy.ExceptionTypeName(ex);
}
