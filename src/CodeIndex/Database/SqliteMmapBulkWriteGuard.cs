using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

/// <summary>
/// Temporarily disables SQLite memory-mapped I/O while a high-churn index run holds its
/// largest managed reference-graph structures. The configured mapping is restored after
/// the write scopes unwind, including failure and cancellation paths.
/// </summary>
internal sealed class SqliteMmapBulkWriteGuard : IDisposable
{
    private readonly long _restoreMmapSizeBytes;
    private SqliteConnection? _connection;

    private SqliteMmapBulkWriteGuard(
        SqliteConnection connection,
        long restoreMmapSizeBytes)
    {
        _connection = connection;
        _restoreMmapSizeBytes = restoreMmapSizeBytes;
    }

    internal static SqliteMmapBulkWriteGuard? Start(
        DbWriter writer,
        bool enabled)
    {
        if (!enabled || !Environment.Is64BitProcess)
            return null;

        var connection = writer.Connection;
        var configuredMmapSizeBytes = ReadMmapSizeBytes(connection);
        if (configuredMmapSizeBytes <= 0)
            return null;

        var appliedMmapSizeBytes = SetMmapSizeBytes(connection, 0);
        return appliedMmapSizeBytes == 0
            ? new SqliteMmapBulkWriteGuard(connection, configuredMmapSizeBytes)
            : null;
    }

    public void Dispose()
    {
        var connection = _connection;
        if (connection == null)
            return;

        try
        {
            SetMmapSizeBytes(connection, _restoreMmapSizeBytes);
        }
        finally
        {
            _connection = null;
        }
    }

    private static long ReadMmapSizeBytes(SqliteConnection connection)
    {
        using var command = SqliteConnectionPolicy.CreateCommand(connection);
        command.CommandText = "PRAGMA mmap_size";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long SetMmapSizeBytes(
        SqliteConnection connection,
        long mmapSizeBytes)
    {
        using var command = SqliteConnectionPolicy.CreateCommand(connection);
        command.CommandText = DbPragmaPolicy.MmapSizePragmaSql(mmapSizeBytes);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
