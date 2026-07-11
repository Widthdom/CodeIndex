using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private static string BuildReferenceInsertSql(int rowCount)
    {
        var sql = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 256);
        sql.Append(@"
                INSERT INTO symbol_references (
                    file_id, symbol_name, reference_kind, line, column_number,
                    context, reference_line_id, container_kind, container_name,
                    symbol_name_folded, container_name_folded, is_self_reference,
                    is_mutual_recursion
                )
                VALUES ");
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");
            sql.Append($@"(
                    @fid{row}, @symbolName{row}, @referenceKind{row}, @line{row}, @columnNumber{row},
                    @context{row}, @referenceLineId{row}, @containerKind{row}, @containerName{row},
                    @symbolNameFolded{row}, @containerNameFolded{row}, @isSelfReference{row},
                    @isMutualRecursion{row}
                )");
        }
        return sql.ToString();
    }

    private static void AddReferenceInsertParameters(SqliteCommand cmd, int rowCount)
    {
        for (var row = 0; row < rowCount; row++)
        {
            cmd.Parameters.Add($"@fid{row}", SqliteType.Integer);
            cmd.Parameters.Add($"@symbolName{row}", SqliteType.Text);
            cmd.Parameters.Add($"@referenceKind{row}", SqliteType.Text);
            cmd.Parameters.Add($"@line{row}", SqliteType.Integer);
            cmd.Parameters.Add($"@columnNumber{row}", SqliteType.Integer);
            cmd.Parameters.Add($"@context{row}", SqliteType.Text);
            cmd.Parameters.Add($"@referenceLineId{row}", SqliteType.Integer);
            cmd.Parameters.Add($"@containerKind{row}", SqliteType.Text);
            cmd.Parameters.Add($"@containerName{row}", SqliteType.Text);
            cmd.Parameters.Add($"@symbolNameFolded{row}", SqliteType.Text);
            cmd.Parameters.Add($"@containerNameFolded{row}", SqliteType.Text);
            cmd.Parameters.Add($"@isSelfReference{row}", SqliteType.Integer);
            cmd.Parameters.Add($"@isMutualRecursion{row}", SqliteType.Integer);
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
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");
            sql.Append($"(@fid{row}, @line{row}, @context{row})");
        }
        return sql.Append(suffix).ToString();
    }

    private static string BuildReferenceLineLookupSql(int rowCount)
    {
        var rows = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 48);
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                rows.Append(", ");
            rows.Append($"(@lookupFid{row}, @lookupLine{row}, @lookupContext{row})");
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

    private static void AddReferenceLineParameters(
        SqliteCommand cmd,
        int rowCount,
        string fileIdPrefix,
        string linePrefix,
        string contextPrefix)
    {
        for (var row = 0; row < rowCount; row++)
        {
            cmd.Parameters.Add($"{fileIdPrefix}{row}", SqliteType.Integer);
            cmd.Parameters.Add($"{linePrefix}{row}", SqliteType.Integer);
            cmd.Parameters.Add($"{contextPrefix}{row}", SqliteType.Text);
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
