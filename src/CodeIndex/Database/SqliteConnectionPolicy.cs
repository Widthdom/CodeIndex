using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal enum SqliteConnectionPolicyMode
{
    Default,
    ReadWrite,
    ReadOnly,
    ImmutableReadOnlyUri,
    Unpooled,
    ReadOnlyUnpooled,
}

internal static class SqliteConnectionPolicy
{
    public const int DefaultCommandTimeoutSeconds = 30;
    public const bool LongRunningCommandsRequireCancellation = true;
    public const string DefaultModeName = "default";
    public const string ReadWriteModeName = "read_write";
    public const string ReadOnlyModeName = "read_only";
    public const string ImmutableReadOnlyUriModeName = "immutable_read_only_uri";
    public const string UnpooledModeName = "unpooled";
    public const string ReadOnlyUnpooledModeName = "read_only_unpooled";

    public static string BuildConnectionString(string dbPath, SqliteOpenMode? mode = null)
        => mode switch
        {
            SqliteOpenMode.ReadWrite => BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadWrite),
            SqliteOpenMode.ReadOnly => BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadOnly),
            null => BuildConnectionString(dbPath, SqliteConnectionPolicyMode.Default),
            _ => BuildBuilder(dbPath, mode: mode).ConnectionString,
        };

    public static string BuildConnectionString(string dbPath, SqliteConnectionPolicyMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        return mode switch
        {
            SqliteConnectionPolicyMode.Default => BuildBuilder(dbPath).ConnectionString,
            SqliteConnectionPolicyMode.ReadWrite => BuildBuilder(dbPath, SqliteOpenMode.ReadWrite).ConnectionString,
            SqliteConnectionPolicyMode.ReadOnly => BuildBuilder(dbPath, SqliteOpenMode.ReadOnly).ConnectionString,
            SqliteConnectionPolicyMode.ImmutableReadOnlyUri => $"Data Source={ToReadOnlyUri(dbPath)};Mode=ReadOnly",
            SqliteConnectionPolicyMode.Unpooled => BuildBuilder(dbPath, pooling: false).ConnectionString,
            SqliteConnectionPolicyMode.ReadOnlyUnpooled => BuildBuilder(dbPath, SqliteOpenMode.ReadOnly, pooling: false).ConnectionString,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown SQLite connection policy mode."),
        };
    }

    public static string ToReadOnlyUri(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            if (!SqliteFileUri.TryValidateBounds(dbPath, out var boundsError))
                throw boundsError ?? new FormatException("Invalid SQLite file URI.");

            return AppendRequiredQueryFlags(EscapeConnectionStringSeparators(dbPath));
        }

        var fileUri = CodeIndex.FileUriPolicy.PathToFileUri(dbPath);
        return AppendRequiredQueryFlags(EscapeConnectionStringSeparators(fileUri));
    }

    public static SqliteCommand CreateCommand(SqliteConnection connection, string? commandText = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
        ConfigureCommand(command);
        if (commandText != null)
            command.CommandText = commandText;
        return command;
    }

    public static void ConfigureCommand(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.CommandTimeout = DefaultCommandTimeoutSeconds;
    }

    public static StatusSqliteConnectionPolicy BuildStatus(
        bool isReadOnly,
        bool readOnlyFallback,
        bool walCheckpointAttempted,
        bool walCheckpointSucceeded,
        bool readOnlyImmutableFallback,
        string? walCheckpointSkippedReason,
        string? walCheckpointFailureReason,
        bool walStaleSnapshotRisk,
        string? walStaleSnapshotReason)
    {
        var mode = readOnlyImmutableFallback
            ? ImmutableReadOnlyUriModeName
            : isReadOnly
                ? ReadOnlyModeName
                : ReadWriteModeName;
        return new StatusSqliteConnectionPolicy
        {
            ActiveMode = mode,
            OpenMode = mode,
            Pooling = true,
            ImmutableUri = readOnlyImmutableFallback,
            CommandTimeoutSeconds = DefaultCommandTimeoutSeconds,
            LongRunningCommandsRequireCancellation = LongRunningCommandsRequireCancellation,
            ReadOnlyFallback = readOnlyFallback,
            WalCheckpointAttempted = walCheckpointAttempted,
            WalCheckpointSucceeded = walCheckpointSucceeded,
            ReadOnlyImmutableFallback = readOnlyImmutableFallback,
            WalCheckpointSkippedReason = walCheckpointSkippedReason,
            WalCheckpointFailureReason = walCheckpointFailureReason,
            WalStaleSnapshotRisk = walStaleSnapshotRisk,
            WalStaleSnapshotReason = walStaleSnapshotReason,
        };
    }

    private static SqliteConnectionStringBuilder BuildBuilder(
        string dbPath,
        SqliteOpenMode? mode = null,
        bool? pooling = null)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = dbPath };
        if (mode.HasValue)
            builder.Mode = mode.Value;
        if (pooling.HasValue)
            builder.Pooling = pooling.Value;
        return builder;
    }

    private static string AppendRequiredQueryFlags(string uriText)
    {
        var result = uriText;
        result = AppendQueryFlagIfMissing(result, "immutable", "1");
        result = AppendQueryFlagIfMissing(result, "mode", "ro");
        return result;
    }

    private static string EscapeConnectionStringSeparators(string uriText)
        => uriText.Replace(";", "%3B", StringComparison.Ordinal);

    private static string AppendQueryFlagIfMissing(string uriText, string name, string value)
    {
        var queryIndex = uriText.IndexOf('?', StringComparison.Ordinal);
        var query = queryIndex < 0 ? string.Empty : uriText[(queryIndex + 1)..];
        if (QueryContainsNameValue(query, name, value))
            return uriText;

        var separator = queryIndex < 0 ? "?" : "&";
        return $"{uriText}{separator}{name}={value}";
    }

    private static bool QueryContainsNameValue(string query, string name, string value)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (equalsIndex <= 0)
                continue;

            var partName = part[..equalsIndex];
            var partValue = part[(equalsIndex + 1)..];
            if (string.Equals(partName, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(partValue, value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
