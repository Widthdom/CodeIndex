using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    internal static Action? HotspotAggregateRefreshExecutingForTesting { get; set; }
    private const string NonTypeReceiverQualifierPrefix = "\u001freceiver:";

    private const string MutualRecursionValueSql = """
        CASE
            WHEN r.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
             AND (
                (
                    r.source_symbol_id IS NOT NULL
                    AND r.target_symbol_id IS NOT NULL
                    AND r.source_symbol_id <> r.target_symbol_id
                    AND EXISTS (
                        SELECT 1
                        FROM symbol_references AS reverse
                        WHERE reverse.source_symbol_id = r.target_symbol_id
                          AND reverse.target_symbol_id = r.source_symbol_id
                          AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                    )
                )
                OR (
                    r.source_symbol_id IS NULL
                    AND r.target_symbol_id IS NULL
                    AND r.is_self_reference = 0
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
                                WHERE reverse.source_symbol_id IS NULL
                                  AND reverse.target_symbol_id IS NULL
                                  AND reverse.is_self_reference = 0
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
                                WHERE reverse.source_symbol_id IS NULL
                                  AND reverse.target_symbol_id IS NULL
                                  AND reverse.is_self_reference = 0
                                  AND reverse.reference_kind IN ('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')
                                  AND reverse.container_name = r.symbol_name COLLATE NOCASE
                                  AND reverse.symbol_name = r.container_name COLLATE NOCASE
                            )
                        )
                    )
                )
             )
            THEN 1
            ELSE 0
        END
        """;

    private const string RefreshReferenceSourceSymbolsSql = """
        UPDATE symbol_references AS r
        SET source_symbol_id = (
            SELECT s.id
            FROM symbols AS s
            WHERE s.file_id = r.file_id
              AND r.container_name IS NOT NULL
              AND r.container_name <> ''
              AND (s.name_folded = r.container_name_folded
                   OR (s.name_folded IS NULL AND s.name = r.container_name COLLATE NOCASE))
              AND r.line BETWEEN COALESCE(s.start_line, s.line) AND COALESCE(s.end_line, s.line)
            ORDER BY (COALESCE(s.end_line, s.line) - COALESCE(s.start_line, s.line)),
                     COALESCE(s.start_line, s.line) DESC,
                     s.id
            LIMIT 1
        )
        """;

    private const string CreateReferenceUniqueFamiliesSql = """
        CREATE TEMP TABLE IF NOT EXISTS reference_unique_symbol_families (
            lang        TEXT NOT NULL,
            name_folded TEXT NOT NULL,
            family_key  TEXT NOT NULL,
            PRIMARY KEY(lang, name_folded)
        ) WITHOUT ROWID
        """;

    private const string RefreshReferenceUniqueFamiliesSql = """
        DELETE FROM temp.reference_unique_symbol_families;

        INSERT INTO temp.reference_unique_symbol_families(lang, name_folded, family_key)
        SELECT target_file.lang,
               s.name_folded,
               MIN(target_file.path || char(31) ||
                   COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                   COALESCE(s.name, '')) AS family_key
        FROM symbols AS s
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE s.name_folded IS NOT NULL
        GROUP BY target_file.lang, s.name_folded
        HAVING COUNT(DISTINCT target_file.path || char(31) ||
                              COALESCE(s.container_qualified_name, s.container_name, '') || char(31) ||
                              COALESCE(s.name, '')) = 1;
        """;

    private const string RefreshReferenceCandidatesSql = """
        DELETE FROM symbol_reference_candidates;

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 0
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE source_file.lang = target_file.lang
          AND r.target_qualifier IS NOT NULL
          AND r.target_qualifier NOT LIKE char(31) || 'receiver:%'
          AND (
              s.container_name = r.target_qualifier COLLATE NOCASE
              OR s.container_qualified_name = r.target_qualifier COLLATE NOCASE
              OR s.container_qualified_name LIKE '%.' || r.target_qualifier COLLATE NOCASE
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 0
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS source ON source.id = r.source_symbol_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE source_file.lang = 'csharp'
          AND target_file.lang = 'csharp'
          AND r.target_qualifier LIKE char(31) || 'receiver:%'
          AND source.signature IS NOT NULL
          AND source.signature <> ''
          AND (
              source.signature LIKE '%(' || COALESCE(s.container_qualified_name, s.container_name, '') || ' ' ||
                  substr(r.target_qualifier, length(char(31) || 'receiver:') + 1) || '%'
              OR source.signature LIKE '%, ' || COALESCE(s.container_qualified_name, s.container_name, '') || ' ' ||
                  substr(r.target_qualifier, length(char(31) || 'receiver:') + 1) || '%'
              OR source.signature LIKE '%(' || COALESCE(s.container_name, '') || ' ' ||
                  substr(r.target_qualifier, length(char(31) || 'receiver:') + 1) || '%'
              OR source.signature LIKE '%, ' || COALESCE(s.container_name, '') || ' ' ||
                  substr(r.target_qualifier, length(char(31) || 'receiver:') + 1) || '%'
          )
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 1
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        JOIN symbols AS source ON source.id = r.source_symbol_id
        WHERE source_file.lang = target_file.lang
          AND r.target_qualifier IS NULL
          AND s.file_id = r.file_id
          AND source.container_name IS NOT NULL
          AND source.container_name <> ''
          AND (
              s.container_name = source.container_name COLLATE NOCASE
              OR s.container_qualified_name = source.container_qualified_name COLLATE NOCASE
          )
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 2
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        JOIN symbols AS source ON source.id = r.source_symbol_id
        WHERE source_file.lang = target_file.lang
          AND r.target_qualifier IS NULL
          AND source.container_qualified_name IS NOT NULL
          AND source.container_qualified_name <> ''
          AND s.container_qualified_name = source.container_qualified_name COLLATE NOCASE
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 3
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        WHERE source_file.lang = target_file.lang
          AND r.target_qualifier IS NULL
          AND s.file_id = r.file_id
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, s.id, 4
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN symbols AS s
          ON s.name_folded IN (
              r.symbol_name_folded,
              CASE WHEN source_file.lang = 'csharp' AND r.reference_kind = 'attribute'
                   THEN r.symbol_name_folded || 'attribute' END
          )
        JOIN files AS target_file ON target_file.id = s.file_id
        JOIN symbols AS source ON source.id = r.source_symbol_id
        WHERE source_file.lang = target_file.lang
          AND r.target_qualifier IS NULL
          AND source.container_name IS NOT NULL
          AND source.container_name <> ''
          AND s.container_name = source.container_name COLLATE NOCASE
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, target.id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN temp.reference_unique_symbol_families AS unique_family
          ON unique_family.lang = source_file.lang
         AND unique_family.name_folded = r.symbol_name_folded
        JOIN symbols AS target ON target.name_folded = unique_family.name_folded
        JOIN files AS target_file
          ON target_file.id = target.file_id
         AND target_file.lang = unique_family.lang
         AND target_file.path || char(31) ||
             COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
             COALESCE(target.name, '') = unique_family.family_key
        WHERE source_file.lang <> 'csharp'
          AND r.target_qualifier IS NULL
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, target.id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN temp.reference_unique_symbol_families AS unique_family
          ON unique_family.lang = 'csharp'
         AND unique_family.name_folded = r.symbol_name_folded
        JOIN symbols AS target ON target.name_folded = unique_family.name_folded
        JOIN files AS target_file
          ON target_file.id = target.file_id
         AND target_file.lang = 'csharp'
         AND target_file.path || char(31) ||
             COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
             COALESCE(target.name, '') = unique_family.family_key
        WHERE source_file.lang = 'csharp'
          AND r.target_qualifier IS NULL
          AND r.reference_kind <> 'instantiate'
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, target.id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN temp.reference_unique_symbol_families AS unique_family
          ON unique_family.lang = 'csharp'
         AND unique_family.name_folded = r.symbol_name_folded || 'attribute'
        JOIN symbols AS target ON target.name_folded = unique_family.name_folded
        JOIN files AS target_file
          ON target_file.id = target.file_id
         AND target_file.lang = 'csharp'
         AND target_file.path || char(31) ||
             COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
             COALESCE(target.name, '') = unique_family.family_key
        WHERE source_file.lang = 'csharp'
          AND r.target_qualifier IS NULL
          AND r.reference_kind = 'attribute'
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
                AND existing.scope_rank < 5
          );

        INSERT INTO symbol_reference_candidates(reference_id, symbol_id, scope_rank)
        SELECT r.id, unique_target.symbol_id, 5
        FROM symbol_references AS r
        JOIN files AS source_file ON source_file.id = r.file_id
        JOIN (
            SELECT MIN(s.id) AS symbol_id, s.name_folded
            FROM symbols AS s
            JOIN files AS target_file ON target_file.id = s.file_id
            WHERE target_file.lang = 'csharp'
              AND s.name_folded IS NOT NULL
              AND s.kind IN ('class', 'struct', 'record')
            GROUP BY s.name_folded
            HAVING COUNT(*) = 1
        ) AS unique_target ON unique_target.name_folded = r.symbol_name_folded
        WHERE source_file.lang = 'csharp'
          AND r.target_qualifier IS NULL
          AND r.reference_kind = 'instantiate'
          AND NOT EXISTS (
              SELECT 1 FROM symbol_reference_candidates AS existing
              WHERE existing.reference_id = r.id
          );

        """;

    private const string RefreshReferenceResolutionSql = """
        UPDATE symbol_references AS r
        SET (target_symbol_id, target_symbol_key, resolution_candidate_count, resolution_state) = (
            SELECT CASE WHEN candidate_count = 1 THEN minimum_symbol_id END,
                   CASE WHEN target_family_count = 1 THEN minimum_target_key END,
                   candidate_count,
                   CASE
                       WHEN candidate_count = 0 THEN 'unresolved'
                       WHEN candidate_count = 1 THEN 'resolved'
                       WHEN target_family_count = 1 THEN 'resolved_group'
                       ELSE 'ambiguous'
                   END
            FROM (
                SELECT COUNT(*) AS candidate_count,
                       MIN(c.symbol_id) AS minimum_symbol_id,
                       COUNT(DISTINCT target_file.lang || char(31) || target_file.path || char(31) ||
                                              COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
                                              COALESCE(target.name, '')) AS target_family_count,
                       MIN(target_file.lang || char(31) || target_file.path || char(31) ||
                           COALESCE(target.container_qualified_name, target.container_name, '') || char(31) ||
                           COALESCE(target.name, '')) AS minimum_target_key
                    FROM symbol_reference_candidates AS c
                    JOIN symbols AS target ON target.id = c.symbol_id
                    JOIN files AS target_file ON target_file.id = target.file_id
                    WHERE c.reference_id = r.id
            ) AS resolution
        );

        UPDATE symbol_references
        SET is_self_reference = CASE
                WHEN source_symbol_id IS NOT NULL
                 AND target_symbol_id IS NOT NULL
                 AND source_symbol_id = target_symbol_id THEN 1
                ELSE 0
            END;
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
        InvalidateReferenceIdentityContractForMutation();

        // If a chunk commits but aggregate refresh is cancelled, readers must fall back to
        // raw references until InitializeSchema performs a complete backfill.
        // aggregate refresh 前に中断した場合は trust bit を残さず raw fallback に降格する。
        var aggregateWasReady = ClearHotspotReferenceAggregateReady();

        int rowsPerStatement = GetRowsPerInsertStatement(columnCount: 14);
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
                    cmd.Parameters[parameterIndex++].Value = (object?)ExtractTargetQualifier(reference) ?? DBNull.Value;
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
        var refreshCheckpoint = HotspotAggregateRefreshExecutingForTesting;
        if (refreshCheckpoint != null)
        {
            var invoked = false;
            _conn.CreateFunction("hotspot_refresh_test_checkpoint", () =>
            {
                if (!invoked)
                {
                    invoked = true;
                    refreshCheckpoint();
                }
                return 0;
            });
        }
        var cmd = RentCommand(
            HotspotReferenceAggregateSql.BuildRefreshSql(singleFile: true, includeTestCheckpoint: refreshCheckpoint != null),
            static command => command.Parameters.Add("@file_id", SqliteType.Integer));
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            var completed = 0;
            foreach (var fileId in fileIds)
            {
                CheckBatchCancellationAndReportProgress(
                    "refresh_hotspot_reference_counts",
                    completed,
                    fileIds.Count,
                    cancellationToken);
                cmd.Parameters["@file_id"].Value = fileId;
                try
                {
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
                {
                    throw new OperationCanceledException(
                        "Hotspot reference aggregate refresh was interrupted.",
                        ex,
                        cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
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
        using var transaction = BeginTransaction(cancellationToken, "refresh reference identities");
        SqliteCommand? createUniqueFamiliesCommand = null;
        SqliteCommand? refreshCommand = null;
        try
        {
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            createUniqueFamiliesCommand = RentCommand(CreateReferenceUniqueFamiliesSql, static _ => { });
            createUniqueFamiliesCommand.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            refreshCommand = RentCommand(
                RefreshReferenceSourceSymbolsSql + ";\n" +
                RefreshReferenceUniqueFamiliesSql + "\n" +
                RefreshReferenceCandidatesSql + "\n" +
                RefreshReferenceResolutionSql + "\n" +
                RefreshMutualRecursionFlagsSql,
                static _ => { });
            // Stamp inside the same transaction, but before the graph refresh so the
            // public SQLite changes() result continues to describe recursion updates.
            // 同一トランザクション内で先に marker を設定し、公開 changes() は再帰更新件数を維持する。
            MarkReferenceIdentityContractReady();
            cancellationToken.ThrowIfCancellationRequested();
            refreshCommand.ExecuteNonQuery();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("Mutual recursion refresh was interrupted.", ex, cancellationToken);
        }
        finally
        {
            if (refreshCommand != null)
                ReleaseCommand(refreshCommand);
            if (createUniqueFamiliesCommand != null)
                ReleaseCommand(createUniqueFamiliesCommand);
        }
    }

    private static string? ExtractTargetQualifier(ReferenceRecord reference)
    {
        if (!string.IsNullOrWhiteSpace(reference.TargetQualifier))
        {
            var explicitQualifier = reference.TargetQualifier.Trim();
            return explicitQualifier.StartsWith("global::", StringComparison.Ordinal)
                ? explicitQualifier["global::".Length..]
                : explicitQualifier;
        }
        if (string.IsNullOrWhiteSpace(reference.Context) || string.IsNullOrWhiteSpace(reference.SymbolName))
            return null;

        var context = reference.Context;
        var occurrence = -1;
        var bestDistance = int.MaxValue;
        for (var searchAt = 0; searchAt <= context.Length - reference.SymbolName.Length;)
        {
            var found = context.IndexOf(reference.SymbolName, searchAt, StringComparison.Ordinal);
            if (found < 0)
                break;
            var distance = Math.Abs((found + 1) - reference.Column);
            if (distance < bestDistance)
            {
                occurrence = found;
                bestDistance = distance;
            }
            searchAt = found + Math.Max(1, reference.SymbolName.Length);
        }

        if (occurrence <= 0)
            return null;
        var dot = occurrence - 1;
        while (dot >= 0 && char.IsWhiteSpace(context[dot]))
            dot--;
        if (dot < 0 || context[dot] != '.')
            return null;
        var end = dot - 1;
        while (end >= 0 && char.IsWhiteSpace(context[end]))
            end--;
        var start = end;
        while (start >= 0 && (char.IsLetterOrDigit(context[start]) || context[start] is '_' or '@'))
            start--;
        var qualifier = context[(start + 1)..(end + 1)].TrimStart('@');
        if (qualifier.Length == 0)
            return null;
        // `this.Member()` is genuinely unqualified with respect to the current container.
        // Other lowercase receivers (for example `service.Process()`) need a non-null marker
        // so the global fallback stays disabled. The resolver may recover a target container
        // only from an explicit `Type receiver` pair in the enclosing symbol signature; the
        // receiver text alone must not participate in type matching because a variable named
        // `worker` is not evidence for type `Worker`.
        // `this.Member()` は現在の container に対して実質 unqualified として扱える。
        // それ以外の小文字 receiver（例: `service.Process()`）は global fallback を無効化する
        // non-null marker として保持する。enclosing symbol signature に明示的な `Type receiver`
        // がある場合だけ container を復元し、変数 `worker` 自体を型 `Worker` の根拠にはしない。
        if (string.Equals(qualifier, "this", StringComparison.Ordinal))
            return null;
        return char.IsUpper(qualifier[0])
            ? qualifier
            : NonTypeReceiverQualifierPrefix + qualifier;
    }
}
