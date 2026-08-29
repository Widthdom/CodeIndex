namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string AuthoritativeFreshRawFileInsertSql = """
        INSERT INTO files (path, lang, size, lines, checksum, modified, generated, indexed_at)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, CURRENT_TIMESTAMP)
        """;
    private const string AuthoritativeFreshReferenceLineIdFloorSql = """
        SELECT CASE
            WHEN (SELECT COUNT(*) FROM sqlite_sequence WHERE name = 'reference_lines') > 1
                THEN -1
            ELSE MAX(
                COALESCE((SELECT MAX(id) FROM reference_lines), 0),
                COALESCE((SELECT MAX(seq) FROM sqlite_sequence WHERE name = 'reference_lines'), 0))
        END
        """;

    internal static string AuthoritativeFreshReferenceLineIdFloorSqlForTesting
        => AuthoritativeFreshReferenceLineIdFloorSql;

    internal sealed partial class AuthoritativeFreshBulkInsertScope
    {
        internal long InsertFile(CodeIndex.Models.FileRecord file)
        {
            ArgumentNullException.ThrowIfNull(file);
            EnsureCanExecute();
            using var interrupt = _writer.RegisterSqliteInterrupt(_cancellationToken);
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.Files,
                rows: 1,
                AuthoritativeFreshRawFileInsertSql,
                expectedParameterCount: 7);
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
                var fileId = lease.ExecuteDone(
                    "insert_files",
                    expectedChangedRows: 1,
                    captureLastInsertRowId: true)
                    ?? throw new InvalidDataException(
                        "Raw SQLite insert_files did not capture a last insert ID.");
                // The fresh scope suspends the only files INSERT trigger and owns the
                // connection synchronously, so no intervening INSERT can replace this ID.
                // fresh scopeは唯一のfiles INSERT triggerを停止しconnectionを同期所有するため、
                // このIDが別INSERTで置き換わる余地はない。
                return fileId;
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
            var maximumRows = GetRowsPerAuthoritativeFreshRawInsertStatement(
                columnCount: 3,
                fixedParameterCount: 1);
            if (statementRows <= 0 || statementRows > maximumRows)
            {
                throw new InvalidOperationException(
                    "Raw reference-line insert requires the authoritative fresh parameter budget "
                    + $"(rows={statementRows}, maximum={maximumRows}).");
            }

            using var interrupt = _writer.RegisterSqliteInterrupt(_cancellationToken);
            var idFloor = ReadReferenceLineIdFloor();
            var firstId = checked(idFloor + 1);
            _ = checked(firstId + statementRows - 1);
            var sql = AuthoritativeFreshReferenceLineInsertSqlCache.GetOrAdd(
                statementRows,
                static count => BuildAuthoritativeFreshReferenceLineInsertSql(count));
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.ReferenceLines,
                statementRows,
                sql,
                expectedParameterCount: checked(statementRows * 3 + 1));
            try
            {
                lease.BindInt64(firstId);
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
                lease.ExecuteDone("insert_reference_lines", statementRows);

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
                        var id = checked(firstId + inputOrdinal);
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

        private long ReadReferenceLineIdFloor()
        {
            var lease = RentStatementLease(
                AuthoritativeFreshRawInsertKind.ReferenceLineIdFloor,
                rows: 1,
                AuthoritativeFreshReferenceLineIdFloorSql,
                expectedParameterCount: 0,
                expectedColumnCount: 1);
            try
            {
                ReportStatementExecution("read_reference_line_id_floor", rows: 1, lease);
                var idFloor = lease.ExecuteInt64Scalar("read_reference_line_id_floor");
                if (idFloor < 0)
                {
                    lease.Discard();
                    throw new InvalidDataException(
                        $"Raw SQLite reference-line ID floor was negative ({idFloor}).");
                }
                return idFloor;
            }
            finally
            {
                lease.Dispose();
            }
        }
    }
}
