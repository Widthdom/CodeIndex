using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    /// <summary>
    /// Insert chunks in batches (FTS index is populated automatically by triggers).
    /// Reuses a prepared statement per batch to avoid per-row command overhead.
    /// チャンクをバッチ挿入する（FTSインデックスはトリガーにより自動で反映される）。
    /// バッチごとにプリペアドステートメントを再利用し、行ごとのコマンド生成コストを回避する。
    /// </summary>
    public void InsertChunks(IReadOnlyList<ChunkRecord> chunks)
        => InsertChunks(chunks, CancellationToken.None);

    public void InsertChunks(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0) return;

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 5);
        for (int i = 0; i < chunks.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("insert_chunks", i, chunks.Count, cancellationToken);
            int end = Math.Min(i + rowsPerStatement, chunks.Count);
            try
            {
                // Only create a batch transaction when not already inside an outer transaction
                // 外部トランザクション内でない場合のみバッチトランザクションを作成
                using var transaction = !IsInTransaction() ? BeginTransaction(cancellationToken, "insert chunks") : null;
                InsertChunkBatch(chunks, i, end);
                transaction?.Commit();
            }
            catch (SqliteException batchException) when (IsRowSkippableSqliteException(batchException))
            {
                InsertChunksWithRowSkip(chunks, i, end, batchException, cancellationToken);
            }
        }
        CheckBatchCancellationAndReportProgress("insert_chunks", chunks.Count, chunks.Count, cancellationToken);
    }

    /// <summary>
    /// Insert symbols in batches.
    /// Reuses a prepared statement per batch to avoid per-row command overhead.
    /// シンボルをバッチ挿入する。
    /// バッチごとにプリペアドステートメントを再利用し、行ごとのコマンド生成コストを回避する。
    /// </summary>
    public void InsertSymbols(IReadOnlyList<SymbolRecord> symbols)
        => InsertSymbols(symbols, CancellationToken.None);

    public void InsertSymbols(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return;
        InvalidateReferenceIdentityContractForMutation();

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 20);
        var foldedNameCache = CreateFoldedNameCache(
            Math.Min(symbols.Count, rowsPerStatement),
            namesPerRow: 1);
        for (int i = 0; i < symbols.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("insert_symbols", i, symbols.Count, cancellationToken);
            int end = Math.Min(i + rowsPerStatement, symbols.Count);
            try
            {
                // Only create a batch transaction when not already inside an outer transaction
                // 外部トランザクション内でない場合のみバッチトランザクションを作成
                using var transaction = !IsInTransaction() ? BeginTransaction(cancellationToken, "insert symbols") : null;
                InsertSymbolBatch(symbols, i, end, foldedNameCache);
                transaction?.Commit();
            }
            catch (SqliteException batchException) when (IsRowSkippableSqliteException(batchException))
            {
                InsertSymbolsWithRowSkip(symbols, i, end, batchException, cancellationToken);
            }
        }
        CheckBatchCancellationAndReportProgress("insert_symbols", symbols.Count, symbols.Count, cancellationToken);
    }

    private void InsertChunksWithRowSkip(IReadOnlyList<ChunkRecord> chunks, int start, int end, SqliteException batchException, CancellationToken cancellationToken)
    {
        using var transaction = !IsInTransaction() ? BeginTransaction(cancellationToken, "insert chunks row skip") : null;
        for (int i = start; i < end; i++)
        {
            CheckBatchCancellationAndReportProgress("insert_chunks_row_skip", i, end, cancellationToken);
            var chunk = chunks[i];
            ExecuteWithRowSavepoint(
                () => InsertChunkBatch(chunks, i, i + 1),
                ex => WarnSkippedBatchRow($"chunk file_id={chunk.FileId} chunk_index={chunk.ChunkIndex}", batchException, ex));
        }

        transaction?.Commit();
    }

    private void InsertSymbolsWithRowSkip(IReadOnlyList<SymbolRecord> symbols, int start, int end, SqliteException batchException, CancellationToken cancellationToken)
    {
        using var transaction = !IsInTransaction() ? BeginTransaction(cancellationToken, "insert symbols row skip") : null;
        var foldedNameCache = CreateFoldedNameCache(end - start, namesPerRow: 1);
        for (int i = start; i < end; i++)
        {
            CheckBatchCancellationAndReportProgress("insert_symbols_row_skip", i, end, cancellationToken);
            var symbol = symbols[i];
            ExecuteWithRowSavepoint(
                () => InsertSymbolBatch(symbols, i, i + 1, foldedNameCache),
                ex => WarnSkippedBatchRow($"symbol file_id={symbol.FileId} name={symbol.Name} line={symbol.Line}", batchException, ex));
        }

        transaction?.Commit();
    }

    private void CheckBatchCancellationAndReportProgress(
        string operation,
        int rowsProcessed,
        int rowsTotal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (rowsTotal <= BatchSize && BatchProgressCheckpointForTesting == null)
            return;

        var progress = new DbWriterBatchProgress(operation, rowsProcessed, rowsTotal);
        BatchProgressCheckpointForTesting?.Invoke(progress);
        GlobalToolLog.Info(
            "db_writer_batch_checkpoint"
            + $" operation={operation}"
            + $" rows_processed={rowsProcessed.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $" rows_total={rowsTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private void ExecuteWithRowSavepoint(Action insertRow, Action<Exception> onSkip)
    {
        var savepointName = $"row_skip_{Interlocked.Increment(ref _rowSkipSavepointCounter)}";
        Execute($"SAVEPOINT {savepointName}");
        try
        {
            insertRow();
            Execute($"RELEASE SAVEPOINT {savepointName}");
        }
        catch (Exception ex) when (ex is SqliteException)
        {
            Execute($"ROLLBACK TO SAVEPOINT {savepointName}");
            Execute($"RELEASE SAVEPOINT {savepointName}");
            if (IsRowSkippableSqliteException((SqliteException)ex))
                onSkip(ex);
            else
                throw;
        }
    }

    private static bool IsRowSkippableSqliteException(SqliteException ex)
        => ex.SqliteErrorCode == SqliteConstraintErrorCode;

    private void WarnSkippedBatchRow(string rowIdentifier, Exception batchException, Exception rowException)
    {
        Interlocked.Increment(ref _batchRowsSkipped);
        var message = BuildBatchRowSkipWarning(rowIdentifier, batchException, rowException);
        var testSink = BatchRowSkipWarningForTesting;
        if (testSink != null)
            testSink(message);
        else
            CommandErrorWriter.WriteStderr(message);
    }

    internal static string BuildBatchRowSkipWarningForTesting(string rowIdentifier, Exception batchException, Exception rowException)
        => BuildBatchRowSkipWarning(rowIdentifier, batchException, rowException);

    private static string BuildBatchRowSkipWarning(string rowIdentifier, Exception batchException, Exception rowException)
        => $"Warning: skipped failed batch row ({FormatBatchRowSkipDiagnosticValue(rowIdentifier)}); batch_error={FormatBatchRowSkipException(batchException)}; row_error={FormatBatchRowSkipException(rowException)}";

    private static string FormatBatchRowSkipException(Exception ex)
        => FormatBatchRowSkipDiagnosticValue(DiagnosticRedactor.FormatExceptionMessage(ex));

    private static string FormatBatchRowSkipDiagnosticValue(string? value)
    {
        return ConsoleUi.FormatBoundedValue(value)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal);
    }

    private void InsertChunkBatch(IReadOnlyList<ChunkRecord> chunks, int start, int end)
    {
        var batchCount = end - start;
        var sql = ChunkInsertSqlCache.GetOrAdd(batchCount, static count => BuildChunkInsertSql(count));
        var cmd = RentCommand(sql, c => AddChunkInsertParameters(c, batchCount));
        try
        {
            var parameterIndex = 0;
            for (int j = start; j < end; j++)
            {
                var chunk = chunks[j];
                cmd.Parameters[parameterIndex++].Value = chunk.FileId;
                cmd.Parameters[parameterIndex++].Value = chunk.ChunkIndex;
                cmd.Parameters[parameterIndex++].Value = chunk.StartLine;
                cmd.Parameters[parameterIndex++].Value = chunk.EndLine;
                cmd.Parameters[parameterIndex++].Value = chunk.Content;
            }

            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private void InsertSymbolBatch(IReadOnlyList<SymbolRecord> symbols, int start, int end, Dictionary<string, string?> foldedNameCache)
    {
        var batchCount = end - start;
        var sql = SymbolInsertSqlCache.GetOrAdd(batchCount, static count => BuildSymbolInsertSql(count));
        var cmd = RentCommand(sql, c => AddSymbolInsertParameters(c, batchCount));
        try
        {
            var parameterIndex = 0;
            for (int j = start; j < end; j++)
            {
                var symbol = symbols[j];
                ValidateSymbolKinds(symbol);
                var startLine = symbol.StartLine > 0 ? symbol.StartLine : symbol.Line;
                var endLine = symbol.EndLine > 0 ? symbol.EndLine : startLine;
                cmd.Parameters[parameterIndex++].Value = symbol.FileId;
                cmd.Parameters[parameterIndex++].Value = symbol.Kind;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.SubKind ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = symbol.Name;
                cmd.Parameters[parameterIndex++].Value = symbol.Line;
                cmd.Parameters[parameterIndex++].Value = startLine;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.StartColumn ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = endLine;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.BodyStartLine ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.BodyEndLine ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.Signature ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.ContainerKind ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.ContainerName ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.ContainerQualifiedName ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.FamilyKey ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.Visibility ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.ReturnType ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = symbol.IsMetadataTarget.HasValue
                    ? (symbol.IsMetadataTarget.Value ? 1 : 0)
                    : (object)DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = (object?)symbol.MetadataTargetSource ?? DBNull.Value;
                cmd.Parameters[parameterIndex++].Value = FoldedNameDbValue(symbol.Name, foldedNameCache);
            }

            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private static string BuildChunkInsertSql(int rowCount)
    {
        var sql = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 64);
        sql.Append("INSERT INTO chunks (file_id, chunk_index, start_line, end_line, content) VALUES ");
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");
            AppendBatchParameterTuple(sql, ref parameterIndex, columnCount: 5);
        }
        return sql.ToString();
    }

    private static void AddChunkInsertParameters(SqliteCommand cmd, int rowCount)
    {
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
        }
    }

    private static string BuildSymbolInsertSql(int rowCount)
    {
        var sql = CreateBatchSqlBuilder(rowCount, estimatedCharsPerRow: 320);
        sql.Append(@"
                INSERT INTO symbols (
                    file_id, kind, sub_kind, name, line, start_line, start_column, end_line,
                    body_start_line, body_end_line, signature,
                    container_kind, container_name, container_qualified_name, family_key,
                    visibility, return_type,
                    is_metadata_target, metadata_target_source,
                    name_folded
                )
                VALUES ");
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
                sql.Append(", ");
            AppendBatchParameterTuple(sql, ref parameterIndex, columnCount: 20);
        }
        return sql.ToString();
    }

    private static void AddSymbolInsertParameters(SqliteCommand cmd, int rowCount)
    {
        var parameterIndex = 0;
        for (var row = 0; row < rowCount; row++)
        {
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Integer);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
            AddBatchParameter(cmd, ref parameterIndex, SqliteType.Text);
        }
    }
}
