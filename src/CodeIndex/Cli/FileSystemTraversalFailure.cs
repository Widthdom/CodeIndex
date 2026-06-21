namespace CodeIndex.Cli;

internal static class FileSystemTraversalFailure
{
    internal static bool IsExpected(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException;

    internal static string DescribeReason(Exception ex)
        => ex switch
        {
            UnauthorizedAccessException => "permissions",
            PathTooLongException => "a path that is too long",
            ArgumentException => "an invalid path",
            NotSupportedException => "an unsupported path",
            _ => "an I/O error",
        };

    internal static string ExceptionTypeName(Exception ex)
        => ex.GetType().Name;
}
