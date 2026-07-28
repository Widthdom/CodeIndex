using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal enum MaintenanceDatabaseFailureKind
{
    Missing,
    Locked,
    SchemaTooNew,
    NotWritable,
    Corrupt,
    NotDatabase,
    Error,
}

internal sealed record MaintenanceDatabaseError(
    string Operation,
    string Message,
    string Hint,
    string ErrorCode,
    string Category,
    int ExitCode,
    string Path,
    bool PathRedacted,
    int? SqliteErrorCode = null,
    int? SqliteExtendedErrorCode = null,
    IReadOnlyList<string>? Details = null,
    bool? DetailsTruncated = null);

internal static class MaintenanceDatabaseErrorClassifier
{
    internal const string Version = "1";
    private const int PathDiagnosticLimit = 512;
    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;

    internal static MaintenanceDatabaseError FromValidation(
        string operation,
        string dbPath,
        bool showPaths,
        bool isNotFound,
        bool isSchemaTooNew,
        SqliteException? sqliteException)
    {
        if (isNotFound)
            return Create(operation, dbPath, showPaths, MaintenanceDatabaseFailureKind.Missing);
        if (isSchemaTooNew)
            return Create(operation, dbPath, showPaths, MaintenanceDatabaseFailureKind.SchemaTooNew);
        if (sqliteException != null)
            return FromException(operation, dbPath, showPaths, sqliteException);

        return ProbeFileState(dbPath) switch
        {
            MaintenanceDatabaseFileState.Missing =>
                Create(operation, dbPath, showPaths, MaintenanceDatabaseFailureKind.Missing),
            MaintenanceDatabaseFileState.InvalidHeader =>
                Create(operation, dbPath, showPaths, MaintenanceDatabaseFailureKind.NotDatabase),
            _ => Create(operation, dbPath, showPaths, MaintenanceDatabaseFailureKind.NotDatabase),
        };
    }

    internal static MaintenanceDatabaseError FromException(
        string operation,
        string dbPath,
        bool showPaths,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var sqlite = FindSqliteException(exception);
        if (sqlite != null)
        {
            var primaryCode = sqlite.SqliteErrorCode != 0
                ? sqlite.SqliteErrorCode
                : sqlite.SqliteExtendedErrorCode & 0xff;
            var kind = primaryCode switch
            {
                5 or 6 => MaintenanceDatabaseFailureKind.Locked,
                8 => MaintenanceDatabaseFailureKind.NotWritable,
                11 => MaintenanceDatabaseFailureKind.Corrupt,
                26 => MaintenanceDatabaseFailureKind.NotDatabase,
                14 when ProbeFileState(dbPath) == MaintenanceDatabaseFileState.Missing =>
                    MaintenanceDatabaseFailureKind.Missing,
                _ => MaintenanceDatabaseFailureKind.Error,
            };
            return Create(
                operation,
                dbPath,
                showPaths,
                kind,
                sqlite.SqliteErrorCode,
                sqlite.SqliteExtendedErrorCode);
        }

        var fileState = ProbeFileState(dbPath);
        if (fileState == MaintenanceDatabaseFileState.Missing)
            return Create(operation, dbPath, showPaths, MaintenanceDatabaseFailureKind.Missing);
        if (fileState == MaintenanceDatabaseFileState.InvalidHeader)
            return Create(operation, dbPath, showPaths, MaintenanceDatabaseFailureKind.NotDatabase);

        return Create(operation, dbPath, showPaths, MaintenanceDatabaseFailureKind.Error);
    }

    internal static MaintenanceDatabaseError Create(
        string operation,
        string dbPath,
        bool showPaths,
        MaintenanceDatabaseFailureKind kind,
        int? sqliteErrorCode = null,
        int? sqliteExtendedErrorCode = null,
        IReadOnlyList<string>? details = null,
        bool? detailsTruncated = null)
    {
        var path = FormatPathForOutput(dbPath, showPaths, out var pathRedacted);
        var safeDetails = FormatDetailsForOutput(details, showPaths);
        var (message, hint, errorCode, category, exitCode) = kind switch
        {
            MaintenanceDatabaseFailureKind.Missing => (
                "database file was not found",
                "Create or refresh the index with `cdidx index <projectPath>`, or import a known-good index archive, then retry the maintenance command.",
                CommandErrorCodes.DbNotFound,
                "database_missing",
                CommandExitCodes.NotFound),
            MaintenanceDatabaseFailureKind.Locked => (
                "database is locked or busy",
                "Wait for the active writer to finish, inspect the index-lock owner diagnostics when available, then retry with backoff.",
                CommandErrorCodes.DbLocked,
                "database_locked",
                CommandExitCodes.TransientDatabaseError),
            MaintenanceDatabaseFailureKind.SchemaTooNew => (
                "database was written by a newer cdidx schema",
                "Use a current cdidx binary, or rebuild the index with this version only after confirming that replacing the newer index is safe.",
                CommandErrorCodes.SchemaTooNew,
                "database_schema_too_new",
                CommandExitCodes.DatabaseError),
            MaintenanceDatabaseFailureKind.NotWritable => (
                "database is not writable",
                "Point `--db` at a writable database and directory, or use a read-only maintenance preview when the command supports one.",
                CommandErrorCodes.DbNotWritable,
                "database_not_writable",
                CommandExitCodes.DatabaseError),
            MaintenanceDatabaseFailureKind.Corrupt => (
                "database is corrupt",
                "Run `cdidx db integrity` if the file remains readable, then rebuild with `cdidx index <projectPath> --rebuild` or import a known-good index archive.",
                CommandErrorCodes.DbIntegrityFailed,
                "database_corrupt",
                CommandExitCodes.DatabaseError),
            MaintenanceDatabaseFailureKind.NotDatabase => (
                "file is not a valid SQLite CodeIndex database",
                "Point `--db` at an index created by cdidx, rebuild it with `cdidx index <projectPath> --rebuild`, or import a known-good index archive.",
                CommandErrorCodes.DbNotDatabase,
                "database_not_a_database",
                CommandExitCodes.DatabaseError),
            _ => (
                "database maintenance operation failed",
                "Check database access and run `cdidx db integrity`; rebuild or import a known-good index if the file is damaged.",
                CommandErrorCodes.DbError,
                "database_error",
                CommandExitCodes.DatabaseError),
        };

        return new MaintenanceDatabaseError(
            operation,
            message,
            hint,
            errorCode,
            category,
            exitCode,
            path,
            pathRedacted,
            sqliteErrorCode,
            sqliteExtendedErrorCode,
            safeDetails,
            detailsTruncated);
    }

    internal static string FormatPathForOutput(string dbPath, bool showPaths)
        => FormatPathForOutput(dbPath, showPaths, out _);

    private static string FormatPathForOutput(string dbPath, bool showPaths, out bool pathRedacted)
    {
        var bounded = DiagnosticRedactor.BoundDiagnosticText(
            SqliteFileUri.TruncateDiagnosticValue(dbPath),
            PathDiagnosticLimit);
        var formatted = DiagnosticRedactor.RedactSensitiveText(
            bounded,
            redactPaths: !showPaths && IsAbsolutePathOrFileUri(bounded));
        pathRedacted = !string.Equals(formatted, bounded, StringComparison.Ordinal);
        return formatted;
    }

    private static bool IsAbsolutePathOrFileUri(string path)
    {
        if (SqliteFileUri.StartsWithFileScheme(path) || Path.IsPathFullyQualified(path))
            return true;

        if (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] is '\\' or '/')
        {
            return true;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string>? FormatDetailsForOutput(
        IReadOnlyList<string>? details,
        bool showPaths)
    {
        if (details == null)
            return null;

        return details
            .Select(detail => DiagnosticRedactor.BoundDiagnosticText(
                DiagnosticRedactor.RedactSensitiveText(detail, redactPaths: !showPaths),
                maxChars: 8192))
            .ToArray();
    }

    private static SqliteException? FindSqliteException(Exception exception)
    {
        if (exception is SqliteException sqlite)
            return sqlite;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                var nested = FindSqliteException(inner);
                if (nested != null)
                    return nested;
            }
        }

        return exception.InnerException == null
            ? null
            : FindSqliteException(exception.InnerException);
    }

    private static MaintenanceDatabaseFileState ProbeFileState(string dbPath)
    {
        try
        {
            var localPath = dbPath;
            if (SqliteFileUri.StartsWithFileScheme(dbPath)
                && (!DbPathResolver.TryNormalizeDbPath(dbPath, out localPath, out _)
                    || string.IsNullOrWhiteSpace(localPath)))
            {
                return MaintenanceDatabaseFileState.Unknown;
            }

            var longPath = LongPath.EnsureWindowsPrefix(localPath);
            if (!File.Exists(longPath))
                return MaintenanceDatabaseFileState.Missing;

            Span<byte> header = stackalloc byte[16];
            using var stream = new FileStream(
                longPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bytesRead = stream.Read(header);
            return bytesRead == header.Length && header.SequenceEqual(SqliteHeader)
                ? MaintenanceDatabaseFileState.SqliteHeader
                : MaintenanceDatabaseFileState.InvalidHeader;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return MaintenanceDatabaseFileState.Unknown;
        }
    }

    private enum MaintenanceDatabaseFileState
    {
        Unknown,
        Missing,
        InvalidHeader,
        SqliteHeader,
    }
}

internal static class MaintenanceDatabaseErrorWriter
{
    internal static int Write(
        bool json,
        JsonSerializerOptions jsonOptions,
        MaintenanceDatabaseError error)
    {
        if (json)
        {
            var payload = new MaintenanceDatabaseErrorJsonResult(
                "error",
                error.Message,
                error.Hint,
                error.ErrorCode,
                error.Category,
                MaintenanceDatabaseErrorClassifier.Version,
                error.Operation,
                error.Path,
                error.PathRedacted,
                error.SqliteErrorCode,
                error.SqliteExtendedErrorCode,
                error.Details,
                error.DetailsTruncated);
            CommandOutputWriter.WriteLine(JsonSerializer.Serialize(
                payload,
                CliJsonSerializerContextFactory.Create(jsonOptions).MaintenanceDatabaseErrorJsonResult));
            return error.ExitCode;
        }

        CommandErrorWriter.WriteStderr($"Error [{error.ErrorCode}]: {error.Message}.");
        CommandErrorWriter.WriteStderr($"Database: {error.Path}");
        CommandErrorWriter.WriteStderr($"Category: {error.Category} (classifier v{MaintenanceDatabaseErrorClassifier.Version})");
        if (error.SqliteErrorCode is { } sqliteCode)
        {
            CommandErrorWriter.WriteStderr(
                $"SQLite result: {sqliteCode}; extended: {error.SqliteExtendedErrorCode ?? sqliteCode}");
        }
        if (error.Details is { Count: > 0 })
        {
            CommandErrorWriter.WriteStderr("Details:");
            foreach (var detail in error.Details)
                CommandErrorWriter.WriteStderr($"  - {detail}");
            if (error.DetailsTruncated == true)
                CommandErrorWriter.WriteStderr("  - <additional details truncated>");
        }
        CommandErrorWriter.WriteStderr($"Hint: {error.Hint}");
        return error.ExitCode;
    }
}
