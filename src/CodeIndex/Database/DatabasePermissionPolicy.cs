using CodeIndex.Cli;
using CodeIndex.Models;

namespace CodeIndex.Database;

internal enum DatabasePermissionPolicyMode
{
    BestEffort,
    Strict,
}

internal interface IDatabaseFileModeProvider
{
    bool SupportsUnixFileModes { get; }
    bool FileExists(string path);
    void SetUnixFileMode(string path, UnixFileMode mode);
    UnixFileMode GetUnixFileMode(string path);
}

internal sealed class SystemDatabaseFileModeProvider : IDatabaseFileModeProvider
{
    public static SystemDatabaseFileModeProvider Instance { get; } = new();

    private SystemDatabaseFileModeProvider()
    {
    }

    public bool SupportsUnixFileModes => !OperatingSystem.IsWindows();

    public bool FileExists(string path) => File.Exists(path);

    public void SetUnixFileMode(string path, UnixFileMode mode)
    {
#pragma warning disable CA1416
        File.SetUnixFileMode(path, mode);
#pragma warning restore CA1416
    }

    public UnixFileMode GetUnixFileMode(string path)
    {
#pragma warning disable CA1416
        return File.GetUnixFileMode(path);
#pragma warning restore CA1416
    }
}

internal static class DatabasePermissionPolicy
{
    public const string EnvironmentVariable = "CDIDX_DB_PERMISSION_POLICY";
    public const string BestEffortName = "best_effort";
    public const string StrictName = "strict";
    public const string FailureCode = "database_permission_hardening_failed";
    public const string InvalidPolicyCode = "invalid_database_permission_policy";

    public static DatabasePermissionPolicyMode Resolve()
    {
        var configured = CdidxEnvironment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
            return DatabasePermissionPolicyMode.BestEffort;

        return configured.Trim().ToLowerInvariant() switch
        {
            BestEffortName or "best-effort" => DatabasePermissionPolicyMode.BestEffort,
            StrictName => DatabasePermissionPolicyMode.Strict,
            _ => throw new CodeIndexException(
                InvalidPolicyCode,
                CodeIndexExceptionCategory.Filesystem,
                $"Invalid {EnvironmentVariable} value.",
                hint: $"Set {EnvironmentVariable} to '{BestEffortName}' (default) or '{StrictName}'."),
        };
    }

    public static string ToName(DatabasePermissionPolicyMode policy)
        => policy == DatabasePermissionPolicyMode.Strict ? StrictName : BestEffortName;

    public static StatusDatabasePermissionDiagnostic CreateDiagnostic(
        string operation,
        string target,
        Exception exception)
    {
        var reason = exception switch
        {
            NotSupportedException => "not_supported",
            UnauthorizedAccessException => "permission_denied",
            IOException => "io_error",
            _ => "unknown",
        };
        var message = reason switch
        {
            "not_supported" => $"The filesystem does not support Unix mode {operation} for the database {target}.",
            "permission_denied" => $"The current user cannot {operation} the Unix mode for the database {target}.",
            _ => $"The filesystem rejected Unix mode {operation} for the database {target}.",
        };
        var recommendedAction = reason switch
        {
            "not_supported" => "Move the database to a filesystem that supports Unix file modes, or set owner-only permissions outside cdidx and accept best-effort enforcement.",
            "permission_denied" => "Grant the current user permission to change the database and sidecar modes, or move the database to a writable filesystem.",
            _ => "Check the filesystem health and mount options, then retry or move the database to a filesystem that supports Unix file modes.",
        };
        return new StatusDatabasePermissionDiagnostic
        {
            Operation = operation,
            Target = target,
            Reason = reason,
            Message = message,
            RecommendedAction = recommendedAction,
        };
    }

    public static CodeIndexException CreateStrictFailure(
        StatusDatabasePermissionDiagnostic diagnostic,
        Exception innerException)
        => new(
            FailureCode,
            CodeIndexExceptionCategory.Filesystem,
            $"Database permission hardening failed in strict mode while attempting to {diagnostic.Operation} the Unix mode for the database {diagnostic.Target} ({diagnostic.Reason}).",
            hint: diagnostic.RecommendedAction
                + $" To continue only after accepting the permission risk, set {EnvironmentVariable}={BestEffortName}.",
            innerException: innerException);
}
