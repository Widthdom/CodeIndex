using System.Text;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const int ReferenceInsertParameterCountPerRow = 14;
    private static readonly AsyncLocal<Action<ReferenceInsertBindingWork>?>
        ScopedReferenceInsertBindingWorkForTesting = new();

    internal sealed record ReferenceInsertBindingWork(
        int StatementRows,
        int BoundParameterCount,
        int MaterializedReferenceCount,
        int MaterializedReferenceLineCount,
        bool UsesFreshResolutionDefaults);

    internal static Action<ReferenceInsertBindingWork>? ReferenceInsertBindingWorkForTesting
    {
        get => ScopedReferenceInsertBindingWorkForTesting.Value;
        set => ScopedReferenceInsertBindingWorkForTesting.Value = value;
    }

    private static string BuildReferenceInsertSql(
        int rowCount,
        bool useFreshReferenceResolutionDefaults,
        bool useMaterializedFreshSourceLookup = false)
    {
        if (useMaterializedFreshSourceLookup && !useFreshReferenceResolutionDefaults)
        {
            throw new ArgumentException(
                "The materialized source lookup is only valid for authoritative fresh reference inserts.",
                nameof(useMaterializedFreshSourceLookup));
        }

        var sql = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 256);
        if (useFreshReferenceResolutionDefaults)
        {
            sql.Append(@"
                WITH fresh_reference(
                    input_ordinal,
                    file_id, symbol_name, reference_kind, line, column_number, span_length,
                    context, reference_line_id, container_kind, container_name,
                    symbol_name_folded, container_name_folded, is_self_reference,
                    is_mutual_recursion, target_qualifier) AS (
                    VALUES ");
            var freshParameterIndex = 0;
            for (var row = 0; row < rowCount; row++)
            {
                if (row > 0)
                    sql.Append(", ");
                AppendReferenceInsertParameterTuple(
                    sql,
                    ref freshParameterIndex,
                    row);
            }
            sql.Append($@"
                )
                INSERT INTO symbol_references (
                    file_id, symbol_name, reference_kind, line, column_number, span_length,
                    context, reference_line_id, container_kind, container_name,
                    symbol_name_folded, container_name_folded, is_self_reference,
                    is_mutual_recursion, target_qualifier, source_symbol_id,
                    resolution_state, resolution_candidate_count)
                SELECT r.file_id,
                       r.symbol_name,
                       r.reference_kind,
                       r.line,
                       r.column_number,
                       r.span_length,
                       r.context,
                       r.reference_line_id,
                       r.container_kind,
                       r.container_name,
                       r.symbol_name_folded,
                       r.container_name_folded,
                       r.is_self_reference,
                       r.is_mutual_recursion,
                       r.target_qualifier,
                       {(useMaterializedFreshSourceLookup
                           ? BuildMaterializedFreshReferenceSourceSymbolValueSql("r")
                           : BuildReferenceSourceSymbolValueSql("r"))},
                       'unresolved',
                       0
                FROM fresh_reference AS r
                ORDER BY r.input_ordinal");
            return sql.ToString();
        }

        sql.Append(@"
                INSERT INTO symbol_references (
                    file_id, symbol_name, reference_kind, line, column_number, span_length,
                    context, reference_line_id, container_kind, container_name,
                    symbol_name_folded, container_name_folded, is_self_reference,
                    is_mutual_recursion, target_qualifier");
        sql.Append(@"
                )
                VALUES ");
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");
            AppendReferenceInsertParameterTuple(
                sql,
                ref parameterIndex);
        }
        return sql.ToString();
    }

    internal static string BuildReferenceInsertSqlForTesting(
        int rowCount,
        bool useFreshReferenceResolutionDefaults,
        bool useMaterializedFreshSourceLookup = false)
        => BuildReferenceInsertSql(
            rowCount,
            useFreshReferenceResolutionDefaults,
            useMaterializedFreshSourceLookup);

    private static void AppendReferenceInsertParameterTuple(
        StringBuilder sql,
        ref int parameterIndex,
        int? inputOrdinal = null)
    {
        sql.Append('(');
        if (inputOrdinal is { } ordinal)
            sql.Append(ordinal).Append(", ");
        for (var column = 0; column < 15; column++)
        {
            if (column > 0)
                sql.Append(", ");
            if (column == 6)
                sql.Append("NULL");
            else
                AppendBatchParameter(sql, ref parameterIndex);
        }
        sql.Append(')');
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
        => BuildReferenceLineValuesInsertSql(
            rowCount,
            " ON CONFLICT(file_id, line, context) DO NOTHING");

    private static string BuildReferenceLineInsertSql(int rowCount)
    {
        var rows = BuildReferenceLineInputRows(rowCount);
        return $@"
                WITH input(input_ordinal, file_id, line, context) AS (
                    VALUES {rows}
                )
                INSERT INTO reference_lines (file_id, line, context)
                SELECT file_id, line, context
                FROM input
                RETURNING id,
                          (SELECT input_ordinal
                           FROM input
                           WHERE input.file_id = reference_lines.file_id
                             AND input.line = reference_lines.line
                             AND input.context = reference_lines.context)";
    }

    internal static string BuildReferenceLineInsertSqlForTesting(int rowCount)
        => BuildReferenceLineInsertSql(rowCount);

    private static string BuildAuthoritativeFreshReferenceLineInsertSql(int rowCount)
    {
        var sql = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 72);
        sql.Append(@"
                WITH input(input_ordinal, file_id, line, context) AS (
                    VALUES ");
        // ?1 is the checked first ID. The remaining slots preserve row/column order.
        var parameterIndex = 1;
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");
            sql.Append('(').Append(row);
            for (var column = 0; column < 3; column++)
            {
                sql.Append(", ");
                AppendBatchParameter(sql, ref parameterIndex);
            }
            sql.Append(')');
        }
        return sql.Append(@"
                )
                INSERT INTO reference_lines (id, file_id, line, context)
                SELECT ?1 + input_ordinal, file_id, line, context
                FROM input").ToString();
    }

    internal static string BuildAuthoritativeFreshReferenceLineInsertSqlForTesting(int rowCount)
        => BuildAuthoritativeFreshReferenceLineInsertSql(rowCount);

    private static string BuildReferenceLineValuesInsertSql(int rowCount, string suffix)
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
        var rows = BuildReferenceLineInputRows(rowCount);
        return $@"
                WITH lookup(input_ordinal, file_id, line, context) AS (
                    VALUES {rows}
                )
                SELECT rl.id, l.input_ordinal
                FROM reference_lines rl
                JOIN lookup l
                 ON l.file_id = rl.file_id
                 AND l.line = rl.line
                 AND l.context = rl.context";
    }

    internal static string BuildReferenceLineLookupSqlForTesting(int rowCount)
        => BuildReferenceLineLookupSql(rowCount);

    private static StringBuilder BuildReferenceLineInputRows(int rowCount)
    {
        var rows = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 64);
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                rows.Append(", ");
            rows.Append('(').Append(row);
            for (var column = 0; column < 3; column++)
            {
                rows.Append(", ");
                AppendBatchParameter(rows, ref parameterIndex);
            }
            rows.Append(')');
        }
        return rows;
    }

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
