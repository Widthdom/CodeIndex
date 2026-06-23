using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal static class SqliteCommandPolicy
{
    private const string SqliteDateTimeTextFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

    public static SqliteParameter AddText(SqliteCommand command, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return AddTyped(command, name, SqliteType.Text, DbType.String, value, value.Length);
    }

    public static SqliteParameter AddNullableText(SqliteCommand command, string name, string? value)
        => AddTyped(command, name, SqliteType.Text, DbType.String, value, value?.Length);

    public static SqliteParameter Add(SqliteCommand command, string name, string? value)
        => AddNullableText(command, name, value);

    public static SqliteParameter AddHashText(SqliteCommand command, string name, string value)
        => AddText(command, name, value);

    public static SqliteParameter AddNullableHashText(SqliteCommand command, string name, string? value)
        => AddNullableText(command, name, value);

    public static SqliteParameter AddInt32(SqliteCommand command, string name, int value)
        => AddTyped(command, name, SqliteType.Integer, DbType.Int32, value);

    public static SqliteParameter AddNullableInt32(SqliteCommand command, string name, int? value)
        => AddTyped(command, name, SqliteType.Integer, DbType.Int32, value);

    public static SqliteParameter Add(SqliteCommand command, string name, int value)
        => AddInt32(command, name, value);

    public static SqliteParameter Add(SqliteCommand command, string name, int? value)
        => AddNullableInt32(command, name, value);

    public static SqliteParameter AddInt64(SqliteCommand command, string name, long value)
        => AddTyped(command, name, SqliteType.Integer, DbType.Int64, value);

    public static SqliteParameter AddNullableInt64(SqliteCommand command, string name, long? value)
        => AddTyped(command, name, SqliteType.Integer, DbType.Int64, value);

    public static SqliteParameter Add(SqliteCommand command, string name, long value)
        => AddInt64(command, name, value);

    public static SqliteParameter Add(SqliteCommand command, string name, long? value)
        => AddNullableInt64(command, name, value);

    public static SqliteParameter AddBoolean(SqliteCommand command, string name, bool value)
        => AddTyped(command, name, SqliteType.Integer, DbType.Int32, value ? 1 : 0);

    public static SqliteParameter AddNullableBoolean(SqliteCommand command, string name, bool? value)
        => AddTyped(command, name, SqliteType.Integer, DbType.Int32, value.HasValue ? (value.Value ? 1 : 0) : null);

    public static SqliteParameter Add(SqliteCommand command, string name, bool value)
        => AddBoolean(command, name, value);

    public static SqliteParameter Add(SqliteCommand command, string name, bool? value)
        => AddNullableBoolean(command, name, value);

    public static SqliteParameter AddDateTime(SqliteCommand command, string name, DateTime value)
    {
        var text = value.ToString(SqliteDateTimeTextFormat, CultureInfo.InvariantCulture);
        return AddTyped(command, name, SqliteType.Text, DbType.String, text, text.Length);
    }

    public static SqliteParameter AddNullableDateTime(SqliteCommand command, string name, DateTime? value)
        => value.HasValue
            ? AddDateTime(command, name, value.Value)
            : AddTyped(command, name, SqliteType.Text, DbType.String, null);

    public static SqliteParameter Add(SqliteCommand command, string name, DateTime value)
        => AddDateTime(command, name, value);

    public static SqliteParameter Add(SqliteCommand command, string name, DateTime? value)
        => AddNullableDateTime(command, name, value);

    public static SqliteParameter AddCopy(SqliteCommand command, SqliteParameter source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateParameterName(source.ParameterName);

        var parameter = AddTyped(command, source.ParameterName, source.SqliteType, source.DbType, source.Value);
        if (source.Size > 0)
            parameter.Size = source.Size;
        return parameter;
    }

    public static SqliteParameter AddLimit(SqliteCommand command, string name, int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "SQLite LIMIT values must be non-negative.");
        return AddInt32(command, name, value);
    }

    public static SqliteParameter AddOffset(SqliteCommand command, string name, int value)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "SQLite OFFSET values must be non-negative.");
        return AddInt32(command, name, value);
    }

    public static int ReadInt32Scalar(SqliteCommand command, string diagnosticName)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ToInt32Scalar(command.ExecuteScalar(), diagnosticName);
    }

    public static int? ReadNullableInt32Scalar(SqliteCommand command, string diagnosticName)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ToNullableInt32Scalar(command.ExecuteScalar(), diagnosticName);
    }

    public static long ReadInt64Scalar(SqliteCommand command, string diagnosticName)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ToInt64Scalar(command.ExecuteScalar(), diagnosticName);
    }

    public static long? ReadNullableInt64Scalar(SqliteCommand command, string diagnosticName)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ToNullableInt64Scalar(command.ExecuteScalar(), diagnosticName);
    }

    public static int ToInt32Scalar(object? value, string diagnosticName)
    {
        var converted = ToInt64Scalar(value, diagnosticName);
        if (converted is < int.MinValue or > int.MaxValue)
            throw ScalarConversionException(diagnosticName, $"outside Int32 range: {converted.ToString(CultureInfo.InvariantCulture)}");
        return (int)converted;
    }

    public static int? ToNullableInt32Scalar(object? value, string diagnosticName)
    {
        var converted = ToNullableInt64Scalar(value, diagnosticName);
        if (!converted.HasValue)
            return null;
        if (converted.Value is < int.MinValue or > int.MaxValue)
            throw ScalarConversionException(diagnosticName, $"outside Int32 range: {converted.Value.ToString(CultureInfo.InvariantCulture)}");
        return (int)converted.Value;
    }

    public static long ToInt64Scalar(object? value, string diagnosticName)
        => ToNullableInt64Scalar(value, diagnosticName)
            ?? throw ScalarConversionException(diagnosticName, "NULL");

    public static long? ToNullableInt64Scalar(object? value, string diagnosticName)
    {
        ValidateDiagnosticName(diagnosticName);
        return value switch
        {
            null or DBNull => null,
            long number => number,
            int number => number,
            short number => number,
            byte number => number,
            sbyte number => number,
            ushort number => number,
            uint number => number,
            ulong number when number <= long.MaxValue => (long)number,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw ScalarConversionException(diagnosticName, $"unsupported value `{FormatScalarValue(value)}`"),
        };
    }

    public static string PragmaSql(string name)
        => $"PRAGMA {SqliteIdentifier.ValidatePragmaName(name)}";

    public static string TableInfoPragmaSql(string tableName)
        => $"PRAGMA table_info({SqliteIdentifier.Quote(tableName)})";

    public static string IndexListPragmaSql(string tableName)
        => $"PRAGMA index_list({SqliteIdentifier.Quote(tableName)})";

    public static string CountRowsSql(string tableName)
        => $"SELECT COUNT(*) FROM {SqliteIdentifier.Quote(tableName)}";

    public static string CountRowsWithLimitSql(string tableName, string limitParameterName)
    {
        ValidateParameterName(limitParameterName);
        return $"SELECT COUNT(*) FROM (SELECT 1 FROM {SqliteIdentifier.Quote(tableName)} LIMIT {limitParameterName})";
    }

    private static SqliteParameter AddTyped(SqliteCommand command, string name, SqliteType sqliteType, DbType dbType, object? value, int? size = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateParameterName(name);
        var parameter = command.Parameters.Add(name, sqliteType);
        parameter.DbType = dbType;
        if (size.HasValue)
            parameter.Size = size.Value;
        parameter.Value = value ?? DBNull.Value;
        return parameter;
    }

    private static void ValidateParameterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("SQLite parameter name must not be empty.", nameof(name));
        if (name[0] is not ('@' or '$' or ':'))
            throw new ArgumentException($"SQLite parameter name must start with `@`, `$`, or `:`: {name}", nameof(name));
    }

    private static void ValidateDiagnosticName(string diagnosticName)
    {
        if (string.IsNullOrWhiteSpace(diagnosticName))
            throw new ArgumentException("SQLite scalar diagnostic name must not be empty.", nameof(diagnosticName));
    }

    private static InvalidDataException ScalarConversionException(string diagnosticName, string reason)
        => new($"SQLite scalar `{diagnosticName}` could not be read as an integer ({reason}).");

    private static string FormatScalarValue(object value)
        => value switch
        {
            string text => text.Length <= 80 ? text : text[..80] + " [truncated]",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name,
        };
}
