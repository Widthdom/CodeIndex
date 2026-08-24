namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string AuthoritativeFreshRawFileInsertSql = """
        INSERT INTO files (path, lang, size, lines, checksum, modified, generated, indexed_at)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, CURRENT_TIMESTAMP)
        RETURNING id
        """;

    internal sealed partial class AuthoritativeFreshBulkInsertScope
    {
        internal long InsertFile(CodeIndex.Models.FileRecord file)
        {
            ArgumentNullException.ThrowIfNull(file);
            EnsureCanExecute();
            using var interrupt = _writer.RegisterSqliteInterrupt(_cancellationToken);
            var sql = TransformReturningSqlForTesting(
                "insert_files",
                statementRows: 1,
                AuthoritativeFreshRawFileInsertSql);
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.Files,
                rows: 1,
                sql,
                expectedParameterCount: 7,
                expectedColumnCount: 1);
            Span<long> returnedIds = stackalloc long[1];
            returnedIds.Clear();
            try
            {
                lease.BindText(file.Path);
                lease.BindNullableText(file.Lang);
                lease.BindInt64(file.Size);
                lease.BindInt64(file.Lines);
                lease.BindNullableText(file.Checksum);
                lease.BindDateTimeText(file.Modified);
                lease.BindInt64(file.Generated ? 1 : 0);

                ReportStatementExecution("insert_files", rows: 1, lease);
                lease.ExecuteReturningRows(
                    "insert_files",
                    expectedRowCount: 1,
                    returnedIds,
                    returnsInputOrdinal: false);
                return returnedIds[0];
            }
            finally
            {
                lease.Dispose();
            }
        }

        internal void InsertReferenceLines(
            IReadOnlyList<(long FileId, int Line, string Context)> rows,
            int start,
            int end,
            IReadOnlyList<int> rowOrdinals,
            ReferenceLineBatchMap lineIds,
            Dictionary<(long FileId, int Line, string Context), long> knownLineIds)
        {
            EnsureCanExecute();
            var statementRows = end - start;
            var maximumRows = GetRowsPerAuthoritativeFreshRawInsertStatement(columnCount: 3);
            if (statementRows <= 0 || statementRows > maximumRows)
            {
                throw new InvalidOperationException(
                    "Raw reference-line RETURNING requires the authoritative fresh parameter budget "
                    + $"(rows={statementRows}, maximum={maximumRows}).");
            }

            using var interrupt = _writer.RegisterSqliteInterrupt(_cancellationToken);
            var baseSql = ReferenceLineInsertSqlCache.GetOrAdd(
                statementRows,
                static count => BuildReferenceLineInsertSql(count));
            var sql = TransformReturningSqlForTesting(
                "insert_reference_lines",
                statementRows,
                baseSql);
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.ReferenceLines,
                statementRows,
                sql,
                expectedParameterCount: statementRows * 3,
                expectedColumnCount: 2);
            Span<long> returnedIds = stackalloc long[statementRows];
            returnedIds.Clear();
            try
            {
                for (var row = start; row < end; row++)
                {
                    var (fileId, line, context) = rows[row];
                    lease.BindInt64(fileId);
                    lease.BindInt64(line);
                    lease.BindText(context);
                }

                ReportStatementExecution("insert_reference_lines", statementRows, lease);
                ReportBatchStatementForTesting(
                    "insert_reference_lines",
                    statementRows,
                    statementRows);
                lease.ExecuteReturningRows(
                    "insert_reference_lines",
                    statementRows,
                    returnedIds,
                    returnsInputOrdinal: true);

                try
                {
                    // Reserve before publishing so ordinary dictionary growth cannot leave a
                    // partially visible set. This dictionary belongs to the current file call;
                    // any exceptional publish still unwinds its per-file SAVEPOINT and the
                    // discarded lease prevents statement reuse.
                    // publish前にcapacityを確保し、current file専用cacheの部分公開を避ける。
                    knownLineIds.EnsureCapacity(checked(knownLineIds.Count + statementRows));
                    for (var inputOrdinal = 0; inputOrdinal < statementRows; inputOrdinal++)
                    {
                        var rowIndex = checked(start + inputOrdinal);
                        var lineOrdinal = rowOrdinals[rowIndex];
                        var key = rows[rowIndex];
                        var id = returnedIds[inputOrdinal];
                        lineIds.SetReferenceLineId(lineOrdinal, id);
                        knownLineIds.Add(key, id);
                    }
                }
                catch
                {
                    lease.Discard();
                    throw;
                }
            }
            finally
            {
                lease.Dispose();
            }
        }

        private static string TransformReturningSqlForTesting(
            string operation,
            int statementRows,
            string sql)
        {
            if (AuthoritativeFreshRawReturningSqlForTesting is not { } transform)
                return sql;

            return transform(new AuthoritativeFreshRawReturningSql(
                    operation,
                    statementRows,
                    sql))
                ?? throw new InvalidDataException(
                    $"Raw SQLite {operation} RETURNING test hook produced no SQL.");
        }
    }
}
