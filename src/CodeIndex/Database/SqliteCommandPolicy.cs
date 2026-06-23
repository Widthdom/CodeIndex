using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal static class SqliteCommandPolicy
{
    public static SqliteParameter AddText(SqliteCommand command, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Add(command, name, SqliteType.Text, value);
    }

    public static SqliteParameter AddNullableText(SqliteCommand command, string name, string? value)
        => Add(command, name, SqliteType.Text, value);

    public static SqliteParameter AddHashText(SqliteCommand command, string name, string value)
        => AddText(command, name, value);

    public static SqliteParameter AddNullableHashText(SqliteCommand command, string name, string? value)
        => AddNullableText(command, name, value);

    public static SqliteParameter AddInt32(SqliteCommand command, string name, int value)
        => Add(command, name, SqliteType.Integer, value);

    public static SqliteParameter AddNullableInt32(SqliteCommand command, string name, int? value)
        => Add(command, name, SqliteType.Integer, value);

    public static SqliteParameter AddInt64(SqliteCommand command, string name, long value)
        => Add(command, name, SqliteType.Integer, value);

    public static SqliteParameter AddNullableInt64(SqliteCommand command, string name, long? value)
        => Add(command, name, SqliteType.Integer, value);

    public static SqliteParameter AddBoolean(SqliteCommand command, string name, bool value)
        => Add(command, name, SqliteType.Integer, value ? 1 : 0);

    public static SqliteParameter AddNullableBoolean(SqliteCommand command, string name, bool? value)
        => Add(command, name, SqliteType.Integer, value.HasValue ? (value.Value ? 1 : 0) : null);

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

    private static SqliteParameter Add(SqliteCommand command, string name, SqliteType type, object? value)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateParameterName(name);
        var parameter = command.Parameters.Add(name, type);
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
