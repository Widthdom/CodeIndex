using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal enum ExistingCodeIndexDbValidationFailure
{
    None,
    Missing,
    Inaccessible,
    InvalidTarget,
    InvalidDatabase,
    SchemaTooNew,
    Exception,
}

public partial class DbContext
{
    internal const string DatabaseOpenMissingCategory = "missing_database";
    internal const string DatabaseOpenPermissionCategory = "permission_denied";
    internal const string DatabaseOpenSidecarCategory = "sidecar_failure";
    internal const string DatabaseOpenInvalidUriCategory = "invalid_uri";
    internal const string DatabaseOpenUnknownCategory = "unknown_open_failure";
    private const int SqliteCantOpenDirtyWal = 14 | (5 << 8);

    public static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        out string message,
        out bool isNotFound,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            requireWritable: true,
            requireSupportedUserVersion: false,
            out message,
            out isNotFound,
            out _,
            cancellationToken);

    internal static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        bool requireWritable,
        bool requireSupportedUserVersion,
        out string message,
        out bool isNotFound,
        out bool isSchemaTooNew,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            requireWritable,
            requireSupportedUserVersion,
            out message,
            out isNotFound,
            out isSchemaTooNew,
            out _,
            out _,
            cancellationToken);

    internal static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        bool requireWritable,
        bool requireSupportedUserVersion,
        out string message,
        out bool isNotFound,
        out bool isSchemaTooNew,
        out ExistingCodeIndexDbValidationFailure validationFailure,
        out Exception? validationException,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            openTarget =>
            {
                var mode = requireWritable
                    ? SqliteConnectionPolicyMode.ReadWrite
                    : SqliteConnectionPolicyMode.ReadOnly;
                return new SqliteConnection(SqliteConnectionPolicy.BuildConnectionString(openTarget, mode));
            },
            static connection => connection.Open(),
            sleep: null,
            requireWritable,
            requireSupportedUserVersion,
            out message,
            out isNotFound,
            out isSchemaTooNew,
            out validationFailure,
            out validationException,
            cancellationToken);

    internal static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        Func<string, SqliteConnection> createConnection,
        Action<SqliteConnection> openConnection,
        Action<int>? sleep,
        out string message,
        out bool isNotFound,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            createConnection,
            openConnection,
            sleep,
            requireWritable: true,
            requireSupportedUserVersion: false,
            out message,
            out isNotFound,
            out _,
            out _,
            out _,
            cancellationToken);

    internal static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        Func<string, SqliteConnection> createConnection,
        Action<SqliteConnection> openConnection,
        Action<int>? sleep,
        out string message,
        out bool isNotFound,
        out Exception? validationException,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            createConnection,
            openConnection,
            sleep,
            requireWritable: true,
            requireSupportedUserVersion: false,
            out message,
            out isNotFound,
            out _,
            out _,
            out validationException,
            cancellationToken);

    private static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        Func<string, SqliteConnection> createConnection,
        Action<SqliteConnection> openConnection,
        Action<int>? sleep,
        bool requireWritable,
        bool requireSupportedUserVersion,
        out string message,
        out bool isNotFound,
        out bool isSchemaTooNew,
        out ExistingCodeIndexDbValidationFailure validationFailure,
        out Exception? validationException,
        CancellationToken cancellationToken = default)
        => ProjectValidationResult(
            ExistingCodeIndexDbValidator.Validate(new ValidationRequest(
                dbPath,
                createConnection,
                openConnection,
                sleep,
                requireWritable,
                requireSupportedUserVersion,
                cancellationToken)),
            out message,
            out isNotFound,
            out isSchemaTooNew,
            out validationFailure,
            out validationException);

    private static bool ProjectValidationResult(
        ValidationResult result,
        out string message,
        out bool isNotFound,
        out bool isSchemaTooNew,
        out ExistingCodeIndexDbValidationFailure validationFailure,
        out Exception? validationException)
    {
        message = result.Message;
        isNotFound = result.IsNotFound;
        isSchemaTooNew = result.IsSchemaTooNew;
        validationFailure = result.Failure;
        validationException = result.Exception;
        return result.IsValid;
    }

    internal static string ClassifyCantOpenFailure(string dbPath, int sqliteExtendedErrorCode)
        => ExistingCodeIndexDbValidator.ClassifyCantOpenFailure(dbPath, sqliteExtendedErrorCode);

    private readonly record struct ValidationRequest(
        string DbPath,
        Func<string, SqliteConnection> CreateConnection,
        Action<SqliteConnection> OpenConnection,
        Action<int>? Sleep,
        bool RequireWritable,
        bool RequireSupportedUserVersion,
        CancellationToken CancellationToken);

    private readonly record struct ValidationResult(
        bool IsValid,
        string Message,
        bool IsNotFound,
        bool IsSchemaTooNew,
        ExistingCodeIndexDbValidationFailure Failure,
        Exception? Exception)
    {
        internal static ValidationResult Valid { get; } = new(
            true, string.Empty, false, false, ExistingCodeIndexDbValidationFailure.None, null);
    }
}
