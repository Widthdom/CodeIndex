using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private static string BuildReferenceInsertSql(int rowCount)
    {
        var sql = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 256);
        sql.Append(@"
                INSERT INTO symbol_references (
                    file_id, symbol_name, reference_kind, line, column_number, span_length,
                    context, reference_line_id, container_kind, container_name,
                    symbol_name_folded, container_name_folded, is_self_reference,
                    is_mutual_recursion, target_qualifier
                )
                VALUES ");
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");
            AppendBatchParameterTuple(sql, ref parameterIndex, columnCount: 15);
        }
        return sql.ToString();
    }

    private static void AddReferenceInsertParameters(SqliteCommand cmd, int rowCount)
    {
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
        }
    }

    private static string BuildReferenceLineUpsertSql(int rowCount)
        => BuildReferenceLineWriteSql(rowCount, " ON CONFLICT(file_id, line, context) DO NOTHING");

    private static string BuildReferenceLineInsertSql(int rowCount)
        => BuildReferenceLineWriteSql(rowCount, " RETURNING id, file_id, line, context");

    private static string BuildReferenceLineWriteSql(int rowCount, string suffix)
    {
        var sql = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 64);
        sql.Append("INSERT INTO reference_lines (file_id, line, context) VALUES ");
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");
            AppendBatchParameterTuple(sql, ref parameterIndex, columnCount: 3);
        }
        return sql.Append(suffix).ToString();
    }

    private static string BuildReferenceLineLookupSql(int rowCount)
    {
        var rows = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 48);
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                rows.Append(", ");
            AppendBatchParameterTuple(rows, ref parameterIndex, columnCount: 3);
        }
        return $@"
                WITH lookup(file_id, line, context) AS (
                    VALUES {rows}
                )
                SELECT rl.id, rl.file_id, rl.line, rl.context
                FROM reference_lines rl
                JOIN lookup l
                  ON l.file_id = rl.file_id
                 AND l.line = rl.line
                 AND l.context = rl.context";
    }

    internal static string BuildReferenceLineLookupSqlForTesting(int rowCount)
        => BuildReferenceLineLookupSql(rowCount);

    private static void AddReferenceLineParameters(
        SqliteCommand cmd,
        int rowCount)
    {
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
        }
    }

    private static void AssignReferenceLineParameterValues(
        SqliteCommand cmd,
        IReadOnlyList<(long FileId, int Line, string Context)> rows,
        int start,
        int end)
    {
        var parameterIndex = 0;
        for (var row = start; row < end; row++)
        {
            var (fileId, line, context) = rows[row];
            cmd.Parameters[parameterIndex++].Value = fileId;
            cmd.Parameters[parameterIndex++].Value = line;
            cmd.Parameters[parameterIndex++].Value = context;
        }
    }
}
