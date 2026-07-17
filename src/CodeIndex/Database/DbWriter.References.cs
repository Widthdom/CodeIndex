using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string MutualRecursionValueSql = """
        CASE
            WHEN r.is_self_reference = 0
             AND r.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
             AND r.container_name IS NOT NULL
             AND r.container_name <> ''
             AND r.symbol_name IS NOT NULL
             AND r.symbol_name <> ''
             AND (
                (
                    r.container_name_folded IS NOT NULL
                    AND r.container_name_folded <> ''
                    AND r.symbol_name_folded IS NOT NULL
                    AND r.symbol_name_folded <> ''
                    AND EXISTS (
                        SELECT 1
                        FROM symbol_references AS reverse
                        WHERE reverse.is_self_reference = 0
                          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                          AND reverse.container_name_folded = r.symbol_name_folded
                          AND reverse.symbol_name_folded = r.container_name_folded
                    )
                )
                OR (
                    (r.container_name_folded IS NULL OR r.symbol_name_folded IS NULL)
                    AND EXISTS (
                        SELECT 1
                        FROM symbol_references AS reverse
                        WHERE reverse.is_self_reference = 0
                          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                          AND reverse.container_name = r.symbol_name COLLATE NOCASE
                          AND reverse.symbol_name = r.container_name COLLATE NOCASE
                    )
                )
             )
            THEN 1
            ELSE 0
        END
        """;

    private static readonly string RefreshMutualRecursionFlagsSql = $"""
        UPDATE symbol_references AS r
        SET is_mutual_recursion = {MutualRecursionValueSql}
        -- IS NOT is null-safe and also normalizes legacy non-boolean values.
        -- IS NOT により NULL と legacy の非boolean値も安全に正規化する。
        WHERE r.is_mutual_recursion IS NOT ({MutualRecursionValueSql})
        """;

    /// <summary>
    /// Insert indexed references in batches.
    /// インデックス済み参照をバッチ挿入する。
    /// </summary>
    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, bool refreshMutualRecursionFlags = true)
        => InsertReferences(references, refreshMutualRecursionFlags, CancellationToken.None);

    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, CancellationToken cancellationToken)
        => InsertReferences(references, refreshMutualRecursionFlags: true, cancellationToken);

    public void InsertReferences(IReadOnlyList<ReferenceRecord> references, bool refreshMutualRecursionFlags, CancellationToken cancellationToken)
        => InsertReferencesCore(references, refreshMutualRecursionFlags, cancellationToken, referenceLinesAreNew: false);

    public void InsertReferencesForNewFiles(IReadOnlyList<ReferenceRecord> references, bool refreshMutualRecursionFlags, CancellationToken cancellationToken)
        => InsertReferencesCore(references, refreshMutualRecursionFlags, cancellationToken, referenceLinesAreNew: true);

    private void InsertReferencesCore(
        IReadOnlyList<ReferenceRecord> references,
        bool refreshMutualRecursionFlags,
        CancellationToken cancellationToken,
        bool referenceLinesAreNew)
    {
        if (references.Count == 0) return;

        // If a chunk commits but aggregate refresh is cancelled, readers must fall back to
        // raw references until InitializeSchema performs a complete backfill.
        // aggregate refresh 前に中断した場合は trust bit を残さず raw fallback に降格する。
        var aggregateWasReady = ClearHotspotReferenceAggregateReady();

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 13);
        var foldedNameCache = CreateFoldedNameCache(
            Math.Min(references.Count, rowsPerStatement),
            namesPerRow: 2);
        var newReferenceLineIds = referenceLinesAreNew
            ? new Dictionary<(long FileId, int Line, string Context), long>()
            : null;
        for (int i = 0; i < references.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("insert_references", i, references.Count, cancellationToken);
            int end = Math.Min(i + rowsPerStatement, references.Count);
            // Always open a chunk-scoped transaction or SAVEPOINT so reference_lines and
            // symbol_references share one rollback boundary; without it a mid-chunk failure
            // under an outer transaction would orphan committed reference_lines (#1518).
            using var transaction = BeginTransaction(cancellationToken, "insert references");
            var referenceLineIds = referenceLinesAreNew
                ? InsertNewReferenceLines(references, i, end, newReferenceLineIds!, cancellationToken)
                : UpsertReferenceLines(references, i, end, cancellationToken);

            var batchCount = end - i;
            var sql = ReferenceInsertSqlCache.GetOrAdd(batchCount, static count => BuildReferenceInsertSql(count));
            var cmd = RentCommand(sql, c => AddReferenceInsertParameters(c, batchCount));
            try
            {
                var parameterIndex = 0;
                (long FileId, int Line, string Context)? previousReferenceLineKey = null;
                var previousReferenceLineId = 0L;
                for (int j = i; j < end; j++)
                {
                    var reference = references[j];
                    ValidateReferenceKinds(reference);
                    var referenceLineKey = (reference.FileId, reference.Line, reference.Context);
                    if (previousReferenceLineKey is not { } previousKey
                        || !ReferenceLineKeysEqual(previousKey, referenceLineKey))
                    {
                        previousReferenceLineId = referenceLineIds[referenceLineKey];
                        previousReferenceLineKey = referenceLineKey;
                    }

                    cmd.Parameters[parameterIndex++].Value = reference.FileId;
                    cmd.Parameters[parameterIndex++].Value = reference.SymbolName;
                    cmd.Parameters[parameterIndex++].Value = reference.ReferenceKind;
                    cmd.Parameters[parameterIndex++].Value = reference.Line;
                    cmd.Parameters[parameterIndex++].Value = reference.Column;
                    cmd.Parameters[parameterIndex++].Value = DBNull.Value;
                    cmd.Parameters[parameterIndex++].Value = previousReferenceLineId;
                    cmd.Parameters[parameterIndex++].Value = (object?)reference.ContainerKind ?? DBNull.Value;
                    cmd.Parameters[parameterIndex++].Value = (object?)reference.ContainerName ?? DBNull.Value;
                    cmd.Parameters[parameterIndex++].Value = FoldedNameDbValue(reference.SymbolName, foldedNameCache);
                    cmd.Parameters[parameterIndex++].Value = FoldedNameDbValue(reference.ContainerName, foldedNameCache);
                    cmd.Parameters[parameterIndex++].Value = reference.IsSelfReference ? 1 : 0;
                    cmd.Parameters[parameterIndex++].Value = reference.IsMutualRecursion ? 1 : 0;
                }

                cmd.ExecuteNonQuery();
            }
            finally
            {
                ReleaseCommand(cmd);
            }
            transaction.Commit();
        }

        CheckBatchCancellationAndReportProgress("insert_references", references.Count, references.Count, cancellationToken);
        RefreshHotspotReferenceCounts(references, cancellationToken);
        RestoreHotspotReferenceAggregateReady(aggregateWasReady);
        if (refreshMutualRecursionFlags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshMutualRecursionFlags(cancellationToken);
        }
    }

    private void RefreshHotspotReferenceCounts(
        IReadOnlyList<ReferenceRecord> references,
        CancellationToken cancellationToken)
    {
        var fileIds = new HashSet<long>();
        foreach (var reference in references)
            fileIds.Add(reference.FileId);

        RefreshHotspotReferenceCounts(fileIds, cancellationToken);
    }

    private void RefreshHotspotReferenceCounts(
        IReadOnlyCollection<long> fileIds,
        CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
            return;

        using var transaction = BeginTransaction(cancellationToken, "refresh hotspot reference counts");
        var cmd = RentCommand(
            HotspotReferenceAggregateSql.BuildRefreshSql(singleFile: true),
            static command => command.Parameters.Add("@file_id", SqliteType.Integer));
        try
        {
            var completed = 0;
            foreach (var fileId in fileIds)
            {
                CheckBatchCancellationAndReportProgress(
                    "refresh_hotspot_reference_counts",
                    completed,
                    fileIds.Count,
                    cancellationToken);
                cmd.Parameters["@file_id"].Value = fileId;
                cmd.ExecuteNonQuery();
                completed++;
            }
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        transaction.Commit();
    }

    private Dictionary<(long FileId, int Line, string Context), long> UpsertReferenceLines(IReadOnlyList<ReferenceRecord> references, int start, int end, CancellationToken cancellationToken)
    {
        var batchCount = end - start;
        var referenceLineKeys = new HashSet<(long FileId, int Line, string Context)>(batchCount);
        (long FileId, int Line, string Context)? previousReferenceLineKey = null;
        for (int i = start; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = references[i];
            var referenceLineKey = (reference.FileId, reference.Line, reference.Context);
            if (previousReferenceLineKey is not { } previousKey
                || !ReferenceLineKeysEqual(previousKey, referenceLineKey))
            {
                referenceLineKeys.Add(referenceLineKey);
                previousReferenceLineKey = referenceLineKey;
            }
        }

        var rows = referenceLineKeys.ToArray();
        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Length; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("upsert_reference_lines", i, rows.Length, cancellationToken);
            int batchEnd = Math.Min(i + rowsPerStatement, rows.Length);
            var statementRowCount = batchEnd - i;
            var sql = ReferenceLineUpsertSqlCache.GetOrAdd(statementRowCount, static count => BuildReferenceLineUpsertSql(count));
            var cmd = RentCommand(sql, c => AddReferenceLineParameters(c, statementRowCount));
            try
            {
                AssignReferenceLineParameterValues(cmd, rows, i, batchEnd);
                cmd.ExecuteNonQuery();
            }
            finally
            {
                ReleaseCommand(cmd);
            }
        }

        var lineIds = new Dictionary<(long FileId, int Line, string Context), long>(rows.Length);
        int keysPerStatement = GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Length; i += keysPerStatement)
        {
            CheckBatchCancellationAndReportProgress("lookup_reference_lines", i, rows.Length, cancellationToken);
            int keyEnd = Math.Min(i + keysPerStatement, rows.Length);
            var statementRowCount = keyEnd - i;
            var sql = ReferenceLineLookupSqlCache.GetOrAdd(statementRowCount, static count => BuildReferenceLineLookupSql(count));
            var cmd = RentCommand(
                sql,
                c => AddReferenceLineParameters(c, statementRowCount));
            try
            {
                AssignReferenceLineParameterValues(cmd, rows, i, keyEnd);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var fileId = reader.GetInt64(1);
                    var line = reader.GetInt32(2);
                    var context = reader.GetString(3);
                    var key = (fileId, line, context);
                    lineIds[key] = id;
                }
            }
            finally
            {
                ReleaseCommand(cmd);
            }
        }

        return lineIds;
    }

    private Dictionary<(long FileId, int Line, string Context), long> InsertNewReferenceLines(
        IReadOnlyList<ReferenceRecord> references,
        int start,
        int end,
        Dictionary<(long FileId, int Line, string Context), long> knownLineIds,
        CancellationToken cancellationToken)
    {
        var batchCount = end - start;
        var referenceLineKeys = new HashSet<(long FileId, int Line, string Context)>(batchCount);
        (long FileId, int Line, string Context)? previousReferenceLineKey = null;
        for (int i = start; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = references[i];
            var referenceLineKey = (reference.FileId, reference.Line, reference.Context);
            if (previousReferenceLineKey is not { } previousKey
                || !ReferenceLineKeysEqual(previousKey, referenceLineKey))
            {
                referenceLineKeys.Add(referenceLineKey);
                previousReferenceLineKey = referenceLineKey;
            }
        }

        var lineIds = new Dictionary<(long FileId, int Line, string Context), long>(referenceLineKeys.Count);
        var rows = new List<(long FileId, int Line, string Context)>(referenceLineKeys.Count);
        foreach (var key in referenceLineKeys)
        {
            if (knownLineIds.TryGetValue(key, out var knownId))
                lineIds[key] = knownId;
            else
                rows.Add(key);
        }

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 3);
        for (int i = 0; i < rows.Count; i += rowsPerStatement)
        {
            CheckBatchCancellationAndReportProgress("insert_reference_lines", i, rows.Count, cancellationToken);
            int batchEnd = Math.Min(i + rowsPerStatement, rows.Count);
            var statementRowCount = batchEnd - i;
            var sql = ReferenceLineInsertSqlCache.GetOrAdd(statementRowCount, static count => BuildReferenceLineInsertSql(count));
            var cmd = RentCommand(sql, c => AddReferenceLineParameters(c, statementRowCount));
            try
            {
                AssignReferenceLineParameterValues(cmd, rows, i, batchEnd);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    var fileId = reader.GetInt64(1);
                    var line = reader.GetInt32(2);
                    var context = reader.GetString(3);
                    var key = (fileId, line, context);
                    lineIds[key] = id;
                    knownLineIds[key] = id;
                }
            }
            finally
            {
                ReleaseCommand(cmd);
            }
        }

        return lineIds;
    }

    private static bool ReferenceLineKeysEqual(
        (long FileId, int Line, string Context) left,
        (long FileId, int Line, string Context) right)
        => left.FileId == right.FileId
           && left.Line == right.Line
           && string.Equals(left.Context, right.Context, StringComparison.Ordinal);

    internal void RefreshMutualRecursionFlags(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MutualRecursionRefreshForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var cmd = RentCommand(RefreshMutualRecursionFlagsSql, static _ => { });
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            cmd.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("Mutual recursion refresh was interrupted.", ex, cancellationToken);
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }
}
