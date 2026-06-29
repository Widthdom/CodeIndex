using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class ExportImportSqliteRow
{
    internal static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
