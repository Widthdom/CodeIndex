using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

internal static class SqliteDynamicSql
{
    internal const int MaxSqlVariables = 999;

    internal static void EnsureParameterBudget(int parameterCount, string context)
    {
        if (parameterCount < 0)
            throw new ArgumentOutOfRangeException(nameof(parameterCount), parameterCount, "Parameter count cannot be negative.");
        if (parameterCount > MaxSqlVariables)
            throw new ArgumentOutOfRangeException(
                nameof(parameterCount),
                parameterCount,
                $"{context} uses {parameterCount} SQLite parameters, exceeding the supported budget of {MaxSqlVariables}.");
    }

    internal static string BuildParameterName(string prefix, int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Parameter index cannot be negative.");

        return $"@{prefix}{index}";
    }

    internal static List<string> BuildParameterNames(string prefix, int count)
    {
        EnsureParameterBudget(count, prefix);
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
            names.Add(BuildParameterName(prefix, i));

        return names;
    }

    internal static string BuildParameterList(string prefix, int count)
        => string.Join(", ", BuildParameterNames(prefix, count));

    internal static List<string> AddParameters<T>(
        SqliteCommand cmd,
        string prefix,
        IReadOnlyList<T> values,
        SqliteType sqliteType,
        string? context = null,
        Func<T, object?>? bindValue = null)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(values);
        EnsureParameterBudget(values.Count, context ?? prefix);

        var names = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var parameterName = BuildParameterName(prefix, i);
            names.Add(parameterName);
            var value = bindValue == null ? values[i] : bindValue(values[i]);
            cmd.Parameters.Add(parameterName, sqliteType).Value = value ?? DBNull.Value;
        }

        return names;
    }
}
