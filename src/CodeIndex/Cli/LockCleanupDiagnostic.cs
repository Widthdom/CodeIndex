namespace CodeIndex.Cli;

internal sealed record LockCleanupDiagnostic(string Component, string Target, string Reason)
{
    internal string ToLogMessage() =>
        $"{Component}_cleanup_failed target={Target} reason={Reason}";

    internal static LockCleanupDiagnostic Create(string component, string target, Exception exception) =>
        new(component, target, ClassifyFailure(exception));

    private static string ClassifyFailure(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "permission_denied",
            FileNotFoundException or DirectoryNotFoundException => "not_found",
            NotSupportedException => "not_supported",
            IOException => "io_error",
            ObjectDisposedException => "object_disposed",
            _ => "operation_failed",
        };
}
