using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbContext
{
    private static class ExistingCodeIndexDbValidator
    {
        internal static ValidationResult Validate(ValidationRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var targetFailure = ValidateTarget(request, out var openTarget);
            if (targetFailure.HasValue)
                return targetFailure.Value;

            var preflightFailure = ValidateFileSystemTarget(request.DbPath, openTarget);
            if (preflightFailure.HasValue)
                return preflightFailure.Value;

            try
            {
                return OpenAndInspect(request, openTarget);
            }
            catch (Exception ex) when (ex is SqliteException or CodeIndexException)
            {
                return ProjectException(request.DbPath, openTarget, ex);
            }
        }

        private static ValidationResult? ValidateTarget(ValidationRequest request, out string openTarget)
        {
            openTarget = request.DbPath;
            if (!SqliteFileUri.StartsWithFileScheme(request.DbPath))
                return null;

            if (!SqliteFileUri.TryValidateBounds(request.DbPath, out var boundsError))
            {
                return Failure(
                    ExistingCodeIndexDbValidationFailure.InvalidTarget,
                    FormatDatabaseOpenFailure(
                        DatabaseOpenInvalidUriCategory,
                        request.DbPath,
                        boundsError?.Message ?? "Invalid SQLite file URI."));
            }

            if (request.RequireWritable && SqliteFileUri.RequestsReadOnly(request.DbPath))
            {
                return Failure(
                    ExistingCodeIndexDbValidationFailure.Inaccessible,
                    $"database must be writable: {request.DbPath}");
            }

            if (!TryGetLocalPath(request.DbPath, out var normalized, out var pathFailureReason)
                || normalized == null)
            {
                return Failure(
                    ExistingCodeIndexDbValidationFailure.InvalidTarget,
                    FormatDatabaseOpenFailure(
                        DatabaseOpenInvalidUriCategory,
                        request.DbPath,
                        pathFailureReason));
            }

            openTarget = normalized;
            return null;
        }

        private static ValidationResult? ValidateFileSystemTarget(string dbPath, string openTarget)
        {
            var preflight = ProbeDatabasePath(openTarget);
            if (preflight is not (DatabasePathProbe.Missing
                or DatabasePathProbe.PermissionDenied
                or DatabasePathProbe.Directory))
            {
                return null;
            }

            var category = preflight switch
            {
                DatabasePathProbe.Missing => DatabaseOpenMissingCategory,
                DatabasePathProbe.PermissionDenied => DatabaseOpenPermissionCategory,
                _ => DatabaseOpenUnknownCategory,
            };
            var failure = preflight switch
            {
                DatabasePathProbe.Missing => ExistingCodeIndexDbValidationFailure.Missing,
                DatabasePathProbe.PermissionDenied => ExistingCodeIndexDbValidationFailure.Inaccessible,
                _ => ExistingCodeIndexDbValidationFailure.InvalidTarget,
            };
            return Failure(
                failure,
                FormatDatabaseOpenFailure(category, dbPath),
                isNotFound: category == DatabaseOpenMissingCategory);
        }

        private static ValidationResult OpenAndInspect(ValidationRequest request, string openTarget)
        {
            using var connection = request.RequireWritable
                ? OpenSqliteConnectionWithRetry(
                    () => request.CreateConnection(openTarget),
                    request.OpenConnection,
                    request.Sleep,
                    dbPath: request.DbPath,
                    cancellationToken: request.CancellationToken)
                : OpenArtifactPreservingQueryOnly(request.DbPath);
            return InspectSchema(connection, request);
        }

        private static ValidationResult InspectSchema(SqliteConnection connection, ValidationRequest request)
        {
            using var command = connection.CreateCommand();
            command.CommandText = SqliteCommandPolicy.PragmaSql("application_id");
            if (SqliteCommandPolicy.ReadInt64Scalar(command, "pragma application_id") != ApplicationId)
                return InvalidDatabase(request.DbPath);

            if (request.RequireSupportedUserVersion)
            {
                command.CommandText = SqliteCommandPolicy.PragmaSql("user_version");
                var userVersion = SqliteCommandPolicy.ReadInt32Scalar(command, "pragma user_version");
                if ((userVersion & ~CurrentSchemaVersion) != 0)
                {
                    return Failure(
                        ExistingCodeIndexDbValidationFailure.SchemaTooNew,
                        $"database was written by a newer cdidx schema stamp (user_version {userVersion}); this binary supports up to {CurrentSchemaVersion}: {request.DbPath}",
                        isSchemaTooNew: true);
                }
            }

            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = command.ExecuteReader();
            var tables = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                tables.Add(reader.GetString(0));

            return RequiredCodeIndexTables.All(tables.Contains)
                ? ValidationResult.Valid
                : InvalidDatabase(request.DbPath);
        }

        private static ValidationResult ProjectException(string dbPath, string openTarget, Exception exception)
        {
            if (exception is SqliteException { SqliteErrorCode: 14 } cantOpen)
            {
                var category = ClassifyCantOpenFailure(openTarget, cantOpen.SqliteExtendedErrorCode);
                return Failure(
                    ExistingCodeIndexDbValidationFailure.Exception,
                    FormatDatabaseOpenFailure(category, dbPath),
                    isNotFound: category == DatabaseOpenMissingCategory,
                    exception: exception);
            }

            return exception is CodeIndexException codeIndexException
                ? Failure(ExistingCodeIndexDbValidationFailure.Exception, codeIndexException.Message, exception: exception)
                : Failure(ExistingCodeIndexDbValidationFailure.Exception, InvalidDatabaseMessage(dbPath), exception: exception);
        }

        internal static string ClassifyCantOpenFailure(string dbPath, int sqliteExtendedErrorCode)
        {
            if (sqliteExtendedErrorCode == SqliteCantOpenDirtyWal)
                return DatabaseOpenSidecarCategory;

            return ProbeDatabasePath(dbPath) switch
            {
                DatabasePathProbe.Missing => DatabaseOpenMissingCategory,
                DatabasePathProbe.PermissionDenied => DatabaseOpenPermissionCategory,
                _ when HasInaccessibleSqliteSidecar(dbPath) => DatabaseOpenSidecarCategory,
                _ => DatabaseOpenUnknownCategory,
            };
        }

        private static bool HasInaccessibleSqliteSidecar(string dbPath)
            => ProbeDatabasePath(dbPath + "-wal") == DatabasePathProbe.PermissionDenied
               || ProbeDatabasePath(dbPath + "-shm") == DatabasePathProbe.PermissionDenied;

        private static DatabasePathProbe ProbeDatabasePath(string path)
        {
            try
            {
                var normalizedPath = LongPath.EnsureWindowsPrefix(path);
                if ((File.GetAttributes(normalizedPath) & FileAttributes.Directory) != 0)
                    return DatabasePathProbe.Directory;

                using var stream = new FileStream(
                    normalizedPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.RandomAccess);
                return DatabasePathProbe.Readable;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return DatabasePathProbe.Missing;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
            {
                return DatabasePathProbe.PermissionDenied;
            }
            catch (IOException)
            {
                return DatabasePathProbe.Unknown;
            }
        }

        private static string FormatDatabaseOpenFailure(string category, string dbPath, string? detail = null)
        {
            var displayPath = dbPath;
            if (SqliteFileUri.StartsWithFileScheme(dbPath))
            {
                var queryIndex = dbPath.IndexOf('?', StringComparison.Ordinal);
                if (queryIndex >= 0)
                    displayPath = dbPath[..queryIndex];
            }

            var pathLabel = DiagnosticSanitizer.ForPath(displayPath);
            var sanitizedDetail = string.IsNullOrWhiteSpace(detail)
                ? null
                : DiagnosticSanitizer.ForMessage(detail);
            var prefix = category == DatabaseOpenMissingCategory
                ? $"database not found [{category}]"
                : $"database open failed [{category}]";
            return sanitizedDetail == null
                ? $"{prefix}: {pathLabel}"
                : $"{prefix}: {pathLabel}; {sanitizedDetail}";
        }

        private static ValidationResult InvalidDatabase(string dbPath)
            => Failure(ExistingCodeIndexDbValidationFailure.InvalidDatabase, InvalidDatabaseMessage(dbPath));

        private static string InvalidDatabaseMessage(string dbPath)
            => $"database is not an existing CodeIndex DB: {dbPath}";

        private static ValidationResult Failure(
            ExistingCodeIndexDbValidationFailure failure,
            string message,
            bool isNotFound = false,
            bool isSchemaTooNew = false,
            Exception? exception = null)
            => new(false, message, isNotFound, isSchemaTooNew, failure, exception);

        private enum DatabasePathProbe
        {
            Readable,
            Missing,
            PermissionDenied,
            Directory,
            Unknown,
        }
    }
}
