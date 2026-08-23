using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    // SQLite numeric parameters address explicit one-origin slots with compact identifiers.
    // Batch values are already assigned by ordinal, so ?1..?N reduce parameter-resolution
    // name work without changing the row/column contract or the statement-size budget.
    // SQLite の numeric parameter はcompactな識別子で1-origin slotを明示する。batch valueは
    // 元からordinal順に設定しているため、?1..?Nならrow/column契約やstatement-size budgetを
    // 変えずにparameter解決時の名前処理を抑えられる。
    private static void AppendBatchParameter(StringBuilder sql, ref int parameterIndex)
        => sql.Append('?').Append(++parameterIndex);

    private static void AppendBatchParameterTuple(StringBuilder sql, ref int parameterIndex, int columnCount)
    {
        sql.Append('(');
        for (var column = 0; column < columnCount; column++)
        {
            if (column > 0)
                sql.Append(", ");
            AppendBatchParameter(sql, ref parameterIndex);
        }
        sql.Append(')');
    }

    private static SqliteParameter AddBatchParameter(SqliteCommand cmd, ref int parameterIndex, SqliteType type)
        => cmd.Parameters.Add($"?{++parameterIndex}", type);
}
