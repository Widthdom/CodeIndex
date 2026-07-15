using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    // SQLite resolves named parameters when a statement is prepared. Dense multi-row
    // inserts can contain thousands of parameters, so semantic names multiplied by a
    // row suffix make cache misses spend substantial time comparing long strings.
    // Values are already assigned by ordinal; compact sequential names preserve that
    // contract while shortening both the SQL text and SQLite's binding work.
    // SQLite は prepare 時に named parameter を解決する。multi-row INSERT では数千個に
    // 達し得るため、行 suffix 付きの長い semantic name は cache miss を重くする。
    // 値は元から ordinal で設定しているので、短い連番名でも順序契約は変わらない。
    private static void AppendBatchParameterTuple(StringBuilder sql, ref int parameterIndex, int columnCount)
    {
        sql.Append('(');
        for (var column = 0; column < columnCount; column++)
        {
            if (column > 0)
                sql.Append(", ");
            sql.Append("@p").Append(parameterIndex++);
        }
        sql.Append(')');
    }

    private static void AddBatchParameter(SqliteCommand cmd, ref int parameterIndex, SqliteType type)
        => cmd.Parameters.Add($"@p{parameterIndex++}", type);
}
