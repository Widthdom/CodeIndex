using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string ReusableStatSnapshotFilterTable = "reusable_stat_snapshot_filter";
    private const string ReusableStatSnapshotCandidatePathIndex = "idx_reusable_stat_snapshot_filter_candidate_path";
    private const string ReusableStatSnapshotExcludedIdIndex = "idx_reusable_stat_snapshot_filter_excluded_id";
    private const int ReusableStatSnapshotFilterBatchSize = 256;
    private static readonly AsyncLocal<Action?> ScopedReusableStatSnapshotReadForTesting = new();
    private static readonly AsyncLocal<Action<int>?> ScopedReusableStatSnapshotInitialCapacityForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedReusableStatSnapshotFilterModeForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedReusableStatSnapshotCandidateRowForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedReusableStatSnapshotFilterBatchForTesting = new();

    internal static Action? ReusableStatSnapshotReadForTesting
    {
        get => ScopedReusableStatSnapshotReadForTesting.Value;
        set => ScopedReusableStatSnapshotReadForTesting.Value = value;
    }

    internal static Action<int>? ReusableStatSnapshotInitialCapacityForTesting
    {
        get => ScopedReusableStatSnapshotInitialCapacityForTesting.Value;
        set => ScopedReusableStatSnapshotInitialCapacityForTesting.Value = value;
    }

    internal static Action<string>? ReusableStatSnapshotFilterModeForTesting
    {
        get => ScopedReusableStatSnapshotFilterModeForTesting.Value;
        set => ScopedReusableStatSnapshotFilterModeForTesting.Value = value;
    }

    internal static Action<string>? ReusableStatSnapshotCandidateRowForTesting
    {
        get => ScopedReusableStatSnapshotCandidateRowForTesting.Value;
        set => ScopedReusableStatSnapshotCandidateRowForTesting.Value = value;
    }

    internal static Action? ReusableStatSnapshotFilterBatchForTesting
    {
        get => ScopedReusableStatSnapshotFilterBatchForTesting.Value;
        set => ScopedReusableStatSnapshotFilterBatchForTesting.Value = value;
    }

    /// <summary>
    /// Check if a file needs re-indexing by comparing modified time and checksum.
    /// 更新日時とチェックサムを比較してファイルの再インデックスが必要か判定する。
    /// Returns the existing file ID if unchanged, or null if indexing is needed.
    /// If the timestamp differs but the checksum matches, updates the timestamp
    /// in the DB and returns the ID (content unchanged, e.g. after git checkout).
    /// 変更なしなら既存ファイルIDを返し、インデックスが必要ならnullを返す。
    /// タイムスタンプが異なってもチェックサムが一致すればDB側を更新しIDを返す。
    /// </summary>
    public long? GetUnchangedFileId(string relativePath, DateTime modified, string? checksum = null, long? size = null, int? lines = null, bool allowReuse = true, string? language = null, bool? generated = null)
    {
        if (!allowReuse)
            return null;
        ReusableUnchangedFileLookupForTesting?.Invoke(relativePath);
        if (!SymbolExtractorVersionMatchesCurrent(language))
            return null;
        if (HasStaleIssueMetadata(relativePath))
            return null;

        // Keep the unchanged check and timestamp touch in one SQLite statement so
        // concurrent row drift cannot slip between a SELECT and a later UPDATE (#1735).
        var cmd = RentCommand(
            @"UPDATE files
              SET modified = CASE
                  WHEN modified <> @modified
                       AND @checksum IS NOT NULL
                       AND checksum = @checksum
                  THEN @modified
                  ELSE modified
              END,
                  size = CASE
                      WHEN @size IS NOT NULL THEN @size
                      ELSE size
                  END,
                  generated = CASE
                  WHEN @generated IS NOT NULL THEN @generated
                  ELSE generated
              END
              WHERE path = @path
                AND (
                    (@checksum IS NOT NULL AND checksum = @checksum AND (@lines IS NULL OR lines = @lines))
                    OR (@checksum IS NULL AND modified = @modified AND (@size IS NULL OR size = @size) AND (@lines IS NULL OR lines = @lines))
                )
              RETURNING id",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@checksum", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
                c.Parameters.Add("@lines", SqliteType.Integer);
                c.Parameters.Add("@generated", SqliteType.Integer);
            });
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            cmd.Parameters["@modified"].Value = modified;
            cmd.Parameters["@checksum"].Value = checksum is null ? DBNull.Value : checksum;
            cmd.Parameters["@size"].Value = size.HasValue ? size.Value : DBNull.Value;
            cmd.Parameters["@lines"].Value = lines.HasValue ? lines.Value : DBNull.Value;
            cmd.Parameters["@generated"].Value = generated.HasValue ? (generated.Value ? 1 : 0) : DBNull.Value;
            var raw = cmd.ExecuteScalar();
            return raw is long id ? id : null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public long? GetReusableUnchangedFileId(
        string relativePath,
        DateTime modified,
        string? checksum,
        long? size,
        int? lines,
        string? language,
        bool? generated,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        bool? generatedExtractionSuppressed,
        bool allowReuse = true)
    {
        if (!allowReuse)
            return null;
        if (!SymbolExtractorVersionMatchesCurrent(language))
            return null;

        var hasIssueMetadataColumns = HasIssueMetadataColumns();
        var isSolutionFile = relativePath.AsSpan().EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
        var staleIssuePredicate = hasIssueMetadataColumns
            ? @"
                AND NOT EXISTS (
                    SELECT 1
                    FROM file_issues stale_i
                    WHERE stale_i.file_id = files.id
                      AND (
                          (stale_i.kind IN ('replacement_char', 'non_utf8_likely', 'bom', 'utf16_bom')
                              AND (stale_i.origin IS NULL OR stale_i.severity IS NULL))
                          OR (stale_i.kind = 'bom' AND @is_solution_file = 1)
                      )
                )"
            : string.Empty;
        var cmd = RentCommand(
            $@"UPDATE files
              SET modified = CASE
                  WHEN modified <> @modified
                       AND @checksum IS NOT NULL
                       AND checksum = @checksum
                  THEN @modified
                  ELSE modified
              END,
                  size = CASE
                      WHEN @size IS NOT NULL THEN @size
                      ELSE size
                  END,
                  generated = CASE
                  WHEN @generated IS NOT NULL THEN @generated
                  ELSE generated
              END
              WHERE path = @path
                AND (
                    (@checksum IS NOT NULL AND checksum = @checksum AND (@lines IS NULL OR lines = @lines))
                    OR (@checksum IS NULL AND modified = @modified AND (@size IS NULL OR size = @size) AND (@lines IS NULL OR lines = @lines))
                )
                {staleIssuePredicate}
                AND NOT EXISTS (
                    SELECT 1
                    FROM symbols
                    WHERE file_id = files.id
                    LIMIT 1 OFFSET @max_symbols
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM file_issues
                    WHERE file_id = files.id
                      AND kind = 'symbol_count_exceeded'
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM symbol_references
                    WHERE file_id = files.id
                    LIMIT 1 OFFSET @max_references
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM file_issues
                    WHERE file_id = files.id
                      AND kind = 'reference_count_exceeded'
                )
                AND (
                    @generated_suppressed IS NULL
                    OR (
                        EXISTS (
                            SELECT 1
                            FROM file_issues
                            WHERE file_id = files.id
                              AND kind = @generated_issue_kind
                        )
                    ) = @generated_suppressed
                )
              RETURNING id",
            c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@checksum", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
                c.Parameters.Add("@lines", SqliteType.Integer);
                c.Parameters.Add("@generated", SqliteType.Integer);
                if (hasIssueMetadataColumns)
                    c.Parameters.Add("@is_solution_file", SqliteType.Integer);
                c.Parameters.Add("@max_symbols", SqliteType.Integer);
                c.Parameters.Add("@max_references", SqliteType.Integer);
                c.Parameters.Add("@generated_suppressed", SqliteType.Integer);
                c.Parameters.Add("@generated_issue_kind", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            cmd.Parameters["@modified"].Value = modified;
            cmd.Parameters["@checksum"].Value = checksum is null ? DBNull.Value : checksum;
            cmd.Parameters["@size"].Value = size.HasValue ? size.Value : DBNull.Value;
            cmd.Parameters["@lines"].Value = lines.HasValue ? lines.Value : DBNull.Value;
            cmd.Parameters["@generated"].Value = generated.HasValue ? (generated.Value ? 1 : 0) : DBNull.Value;
            if (hasIssueMetadataColumns)
                cmd.Parameters["@is_solution_file"].Value = isSolutionFile ? 1 : 0;
            cmd.Parameters["@max_symbols"].Value = maxSymbolsPerFile;
            cmd.Parameters["@max_references"].Value = maxReferencesPerFile;
            cmd.Parameters["@generated_suppressed"].Value = generatedExtractionSuppressed.HasValue
                ? (generatedExtractionSuppressed.Value ? 1 : 0)
                : DBNull.Value;
            cmd.Parameters["@generated_issue_kind"].Value = FileIndexer.GeneratedCodeExtractionSkippedIssueKind;
            var raw = cmd.ExecuteScalar();
            return raw is long id ? id : null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Read-only unchanged lookup for callers that have only filesystem stat data.
    /// filesystem stat だけを持つ呼び出し元向けの read-only 変更なし判定。
    /// </summary>
    public long? GetUnchangedFileIdByStat(
        string relativePath,
        DateTime modified,
        long size,
        string? language,
        bool allowReuse = true)
    {
        if (!allowReuse)
            return null;
        if (!SymbolExtractorVersionMatchesCurrent(language))
            return null;
        if (HasStaleIssueMetadata(relativePath))
            return null;

        var cmd = RentCommand(
            @"SELECT id
              FROM files
              WHERE path = @path
                AND modified = @modified
                AND size = @size
              LIMIT 1",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
            });
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            cmd.Parameters["@modified"].Value = modified;
            cmd.Parameters["@size"].Value = size;
            var raw = cmd.ExecuteScalar();
            return raw is long id ? id : null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public long? GetReusableUnchangedFileIdByStat(
        string relativePath,
        DateTime modified,
        long size,
        string? language,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        bool? generatedExtractionSuppressed,
        bool allowReuse = true)
    {
        if (!allowReuse)
            return null;
        if (!SymbolExtractorVersionMatchesCurrent(language))
            return null;

        var hasIssueMetadataColumns = HasIssueMetadataColumns();
        var isSolutionFile = string.Equals(Path.GetExtension(relativePath), ".sln", StringComparison.OrdinalIgnoreCase);
        var staleIssuePredicate = hasIssueMetadataColumns
            ? @"
                AND NOT EXISTS (
                    SELECT 1
                    FROM file_issues stale_i
                    WHERE stale_i.file_id = f.id
                      AND (
                          (stale_i.kind IN ('replacement_char', 'non_utf8_likely', 'bom', 'utf16_bom')
                              AND (stale_i.origin IS NULL OR stale_i.severity IS NULL))
                          OR (stale_i.kind = 'bom' AND @is_solution_file = 1)
                      )
                )"
            : string.Empty;
        var cmd = RentCommand(
            $@"SELECT f.id
              FROM files f
              WHERE f.path = @path
                AND f.modified = @modified
                AND f.size = @size
                {staleIssuePredicate}
                AND NOT EXISTS (
                    SELECT 1
                    FROM symbols
                    WHERE file_id = f.id
                    LIMIT 1 OFFSET @max_symbols
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM file_issues
                    WHERE file_id = f.id
                      AND kind = 'symbol_count_exceeded'
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM symbol_references
                    WHERE file_id = f.id
                    LIMIT 1 OFFSET @max_references
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM file_issues
                    WHERE file_id = f.id
                      AND kind = 'reference_count_exceeded'
                )
                AND (
                    @generated_suppressed IS NULL
                    OR (
                        EXISTS (
                            SELECT 1
                            FROM file_issues
                            WHERE file_id = f.id
                              AND kind = @generated_issue_kind
                        )
                    ) = @generated_suppressed
                )
              LIMIT 1",
            c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@modified", SqliteType.Text);
                c.Parameters.Add("@size", SqliteType.Integer);
                if (hasIssueMetadataColumns)
                    c.Parameters.Add("@is_solution_file", SqliteType.Integer);
                c.Parameters.Add("@max_symbols", SqliteType.Integer);
                c.Parameters.Add("@max_references", SqliteType.Integer);
                c.Parameters.Add("@generated_suppressed", SqliteType.Integer);
                c.Parameters.Add("@generated_issue_kind", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            cmd.Parameters["@modified"].Value = modified;
            cmd.Parameters["@size"].Value = size;
            if (hasIssueMetadataColumns)
                cmd.Parameters["@is_solution_file"].Value = isSolutionFile ? 1 : 0;
            cmd.Parameters["@max_symbols"].Value = maxSymbolsPerFile;
            cmd.Parameters["@max_references"].Value = maxReferencesPerFile;
            cmd.Parameters["@generated_suppressed"].Value = generatedExtractionSuppressed.HasValue
                ? (generatedExtractionSuppressed.Value ? 1 : 0)
                : DBNull.Value;
            cmd.Parameters["@generated_issue_kind"].Value = FileIndexer.GeneratedCodeExtractionSkippedIssueKind;
            var raw = cmd.ExecuteScalar();
            return raw is long id ? id : null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// Loads repository-wide stat-reuse candidates in one SQLite statement. Callers still
    /// compare every row with a fresh filesystem stat, while avoiding per-file database probes.
    /// Optional retained paths and sorted excluded IDs avoid materializing rows selected for purge.
    /// repository 全体の stat-reuse 候補を 1 回で読み、filesystem stat は各 file で再確認する。
    /// 任意の retained path と昇順 excluded ID により purge 予定行の materialize を避ける。
    /// </summary>
    internal ReusableIndexedFileStatsSnapshot LoadReusableIndexedFileStats(
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        CancellationToken cancellationToken = default,
        int initialCapacity = 0,
        IReadOnlySet<string>? includedPaths = null,
        IReadOnlyList<long>? excludedFileIds = null,
        Action<string>? persistedCSharpPathObserver = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReusableStatSnapshotReadForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        var hasIssuesTable = TableExists("file_issues");
        var hasIssueMetadataColumns = hasIssuesTable && HasIssueMetadataColumns();
        var staleIssuePredicate = hasIssueMetadataColumns
            ? @"
                AND NOT EXISTS (
                    SELECT 1
                    FROM file_issues stale_i
                    WHERE stale_i.file_id = f.id
                      AND (
                          (stale_i.kind IN ('replacement_char', 'non_utf8_likely', 'bom', 'utf16_bom')
                              AND (stale_i.origin IS NULL OR stale_i.severity IS NULL))
                          OR (stale_i.kind = 'bom' AND f.path LIKE '%.sln')
                      )
                )"
            : string.Empty;
        var countIssuePredicates = hasIssuesTable
            ? @"
                AND NOT EXISTS (
                    SELECT 1 FROM file_issues
                    WHERE file_id = f.id AND kind = 'symbol_count_exceeded'
                )
                AND NOT EXISTS (
                    SELECT 1 FROM file_issues
                    WHERE file_id = f.id AND kind = 'reference_count_exceeded'
                )"
            : string.Empty;
        var generatedSuppressionProjection = hasIssuesTable
            ? @"EXISTS (
                    SELECT 1 FROM file_issues
                    WHERE file_id = f.id AND kind = @generated_issue_kind
                )"
            : "0";
        using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
        var sqlFilter = PrepareReusableStatSnapshotFilter(includedPaths, excludedFileIds, cancellationToken);
        SqliteCommand cmd;
        try
        {
            cmd = RentCommand(
                $@"SELECT
                    f.id,
                    f.path,
                    f.modified,
                    f.size,
                    f.lang,
                    {generatedSuppressionProjection} AS generated_suppressed,
                    CASE WHEN
                        f.lang IS NOT NULL
                        AND typeof(f.modified) = 'text'
                        AND typeof(f.size) = 'integer'
                        AND f.size >= 0
                        {staleIssuePredicate}
                        AND NOT EXISTS (
                            SELECT 1 FROM symbols
                            WHERE file_id = f.id
                            LIMIT 1 OFFSET @max_symbols
                        )
                        {countIssuePredicates}
                        AND NOT EXISTS (
                            SELECT 1 FROM symbol_references
                            WHERE file_id = f.id
                            LIMIT 1 OFFSET @max_references
                        )
                    THEN 1 ELSE 0 END AS reusable_eligible
                {sqlFilter.FromSql}
                {sqlFilter.WhereSql}",
                c =>
                {
                    c.Parameters.Add("@max_symbols", SqliteType.Integer);
                    c.Parameters.Add("@max_references", SqliteType.Integer);
                    if (hasIssuesTable)
                        c.Parameters.Add("@generated_issue_kind", SqliteType.Text);
                });
        }
        catch
        {
            if (sqlFilter.UsesTempTable)
                TryClearReusableStatSnapshotFilter();
            throw;
        }
        var currentVersionByLanguage = new Dictionary<string, bool>(StringComparer.Ordinal);
        var reusableInitialCapacity = includedPaths == null
            ? initialCapacity
            : Math.Min(initialCapacity, includedPaths.Count);
        ReusableStatSnapshotInitialCapacityForTesting?.Invoke(reusableInitialCapacity);
        var reusable = new ReusableIndexedFileStatsSnapshot(reusableInitialCapacity);
        try
        {
            cmd.Parameters["@max_symbols"].Value = maxSymbolsPerFile;
            cmd.Parameters["@max_references"].Value = maxReferencesPerFile;
            if (hasIssuesTable)
                cmd.Parameters["@generated_issue_kind"].Value = FileIndexer.GeneratedCodeExtractionSkippedIssueKind;
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileId = reader.GetInt64(0);
                var path = reader.GetString(1);
                ReusableStatSnapshotCandidateRowForTesting?.Invoke(path);
                var language = reader.IsDBNull(4) ? null : reader.GetString(4);
                if (language == "csharp")
                    persistedCSharpPathObserver?.Invoke(path);
                var rawSize = reader.GetValue(3);
                var sizeKnown = rawSize is long && (long)rawSize >= 0;
                var persistedSize = sizeKnown ? (long)rawSize : 0;
                if (reader.GetInt64(6) == 0)
                {
                    reusable.RecordNonReusablePersistedSize(path, sizeKnown, sizeKnown ? persistedSize : 0);
                    continue;
                }

                // reusable_eligible guarantees a non-null language.
                var reusableLanguage = language!;
                if (!currentVersionByLanguage.TryGetValue(reusableLanguage, out var versionCurrent))
                {
                    versionCurrent = SymbolExtractorVersionMatchesCurrent(reusableLanguage);
                    currentVersionByLanguage.Add(reusableLanguage, versionCurrent);
                }

                if (!versionCurrent
                    || !TryParseStoredModifiedUtc(reader.GetString(2), out var modifiedUtc))
                {
                    reusable.RecordNonReusablePersistedSize(path, sizeKnown, sizeKnown ? persistedSize : 0);
                    continue;
                }

                reusable.RecordReusable(path, new ReusableIndexedFileStat(
                    fileId,
                    modifiedUtc,
                    persistedSize,
                    reusableLanguage,
                    reader.GetInt64(5) != 0));
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException("Reusable file stat snapshot was interrupted.", ex, cancellationToken);
        }
        finally
        {
            ReleaseCommand(cmd);
            if (sqlFilter.UsesTempTable)
                TryClearReusableStatSnapshotFilter();
        }

        return reusable;
    }

    private ReusableStatSnapshotSqlFilter PrepareReusableStatSnapshotFilter(
        IReadOnlySet<string>? includedPaths,
        IReadOnlyList<long>? excludedFileIds,
        CancellationToken cancellationToken)
    {
        var hasIncludedPathFilter = includedPaths != null;
        var hasExcludedIdFilter = excludedFileIds is { Count: > 0 };
        if (!hasIncludedPathFilter && !hasExcludedIdFilter)
        {
            ReusableStatSnapshotFilterModeForTesting?.Invoke("none");
            return new ReusableStatSnapshotSqlFilter("FROM files AS f", string.Empty, UsesTempTable: false);
        }

        try
        {
            using var setupTransaction = !IsInTransaction()
                ? BeginTransaction(cancellationToken, "prepare reusable stat snapshot filter")
                : null;
            cancellationToken.ThrowIfCancellationRequested();
            using (var create = _conn.CreateCommand())
            {
                create.Transaction = _activeTransaction;
                create.CommandText = $"""
                    DROP TABLE IF EXISTS temp.{ReusableStatSnapshotFilterTable};
                    CREATE TEMP TABLE {ReusableStatSnapshotFilterTable} (
                        filter_kind INTEGER NOT NULL,
                        candidate_path TEXT,
                        excluded_file_id INTEGER
                    );
                    """;
                create.ExecuteNonQuery();
            }

            if (includedPaths != null)
                InsertReusableStatSnapshotCandidatePaths(includedPaths, cancellationToken);
            if (excludedFileIds is { Count: > 0 })
                InsertReusableStatSnapshotExcludedIds(excludedFileIds, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            using (var createIndexes = _conn.CreateCommand())
            {
                createIndexes.Transaction = _activeTransaction;
                createIndexes.CommandText = $"""
                    CREATE UNIQUE INDEX temp.{ReusableStatSnapshotCandidatePathIndex}
                        ON {ReusableStatSnapshotFilterTable}(candidate_path)
                        WHERE filter_kind = 0;
                    CREATE UNIQUE INDEX temp.{ReusableStatSnapshotExcludedIdIndex}
                        ON {ReusableStatSnapshotFilterTable}(excluded_file_id)
                        WHERE filter_kind = 1;
                    """;
                createIndexes.ExecuteNonQuery();
            }
            setupTransaction?.Commit();

            if (hasIncludedPathFilter)
            {
                ReusableStatSnapshotFilterModeForTesting?.Invoke("candidate_paths");
                return new ReusableStatSnapshotSqlFilter(
                    $"""
                    FROM temp.{ReusableStatSnapshotFilterTable} AS candidate INDEXED BY {ReusableStatSnapshotCandidatePathIndex}
                    CROSS JOIN files AS f ON f.path = candidate.candidate_path
                    LEFT JOIN temp.{ReusableStatSnapshotFilterTable} AS excluded INDEXED BY {ReusableStatSnapshotExcludedIdIndex}
                        ON excluded.filter_kind = 1
                       AND excluded.excluded_file_id = f.id
                    """,
                    "WHERE candidate.filter_kind = 0 AND excluded.excluded_file_id IS NULL",
                    UsesTempTable: true);
            }

            ReusableStatSnapshotFilterModeForTesting?.Invoke("excluded_ids");
            return new ReusableStatSnapshotSqlFilter(
                $"""
                FROM files AS f
                LEFT JOIN temp.{ReusableStatSnapshotFilterTable} AS excluded INDEXED BY {ReusableStatSnapshotExcludedIdIndex}
                    ON excluded.filter_kind = 1
                   AND excluded.excluded_file_id = f.id
                """,
                "WHERE excluded.excluded_file_id IS NULL",
                UsesTempTable: true);
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            TryClearReusableStatSnapshotFilter();
            throw new OperationCanceledException("Reusable file stat snapshot filter preparation was interrupted.", ex, cancellationToken);
        }
        catch
        {
            TryClearReusableStatSnapshotFilter();
            throw;
        }
    }

    private void InsertReusableStatSnapshotCandidatePaths(
        IReadOnlySet<string> includedPaths,
        CancellationToken cancellationToken)
    {
        var batch = new List<string>(Math.Min(ReusableStatSnapshotFilterBatchSize, includedPaths.Count));
        foreach (var path in includedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch.Add(path);
            if (batch.Count < ReusableStatSnapshotFilterBatchSize)
                continue;

            InsertReusableStatSnapshotFilterBatch(batch, filterKind: 0, cancellationToken);
            batch.Clear();
        }

        if (batch.Count > 0)
            InsertReusableStatSnapshotFilterBatch(batch, filterKind: 0, cancellationToken);
    }

    private void InsertReusableStatSnapshotExcludedIds(
        IReadOnlyList<long> excludedFileIds,
        CancellationToken cancellationToken)
    {
        for (var start = 0; start < excludedFileIds.Count; start += ReusableStatSnapshotFilterBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(ReusableStatSnapshotFilterBatchSize, excludedFileIds.Count - start);
            using var insert = _conn.CreateCommand();
            insert.Transaction = _activeTransaction;
            var sql = new System.Text.StringBuilder(
                $"INSERT INTO temp.{ReusableStatSnapshotFilterTable}(filter_kind, excluded_file_id) VALUES ");
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                    sql.Append(',');
                var parameterName = $"@id{index}";
                sql.Append("(1,").Append(parameterName).Append(')');
                insert.Parameters.Add(parameterName, SqliteType.Integer).Value = excludedFileIds[start + index];
            }
            insert.CommandText = sql.ToString();
            ReusableStatSnapshotFilterBatchForTesting?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            insert.ExecuteNonQuery();
        }
    }

    private void InsertReusableStatSnapshotFilterBatch(
        IReadOnlyList<string> paths,
        int filterKind,
        CancellationToken cancellationToken)
    {
        using var insert = _conn.CreateCommand();
        insert.Transaction = _activeTransaction;
        var sql = new System.Text.StringBuilder(
            $"INSERT INTO temp.{ReusableStatSnapshotFilterTable}(filter_kind, candidate_path) VALUES ");
        for (var index = 0; index < paths.Count; index++)
        {
            if (index > 0)
                sql.Append(',');
            var parameterName = $"@path{index}";
            sql.Append('(').Append(filterKind).Append(',').Append(parameterName).Append(')');
            insert.Parameters.Add(parameterName, SqliteType.Text).Value = paths[index];
        }
        insert.CommandText = sql.ToString();
        ReusableStatSnapshotFilterBatchForTesting?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        insert.ExecuteNonQuery();
    }

    private void TryClearReusableStatSnapshotFilter()
    {
        try
        {
            using var drop = _conn.CreateCommand();
            drop.Transaction = _activeTransaction;
            drop.CommandText = $"DROP TABLE IF EXISTS temp.{ReusableStatSnapshotFilterTable}";
            drop.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Best effort during preparation failure; the next call drops the table first.
        }
    }

    private readonly record struct ReusableStatSnapshotSqlFilter(
        string FromSql,
        string WhereSql,
        bool UsesTempTable);

    private static bool TryParseStoredModifiedUtc(string value, out DateTime modifiedUtc)
        => DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal
            | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out modifiedUtc);

    private bool HasStaleIssueMetadata(string relativePath)
    {
        if (!HasIssueMetadataColumns())
        {
            return false;
        }

        var isSolutionFile = string.Equals(Path.GetExtension(relativePath), ".sln", StringComparison.OrdinalIgnoreCase);
        var cmd = RentCommand(
            @"
            SELECT 1
            FROM file_issues i
            JOIN files f ON i.file_id = f.id
            WHERE f.path = @path
              AND (
                  (i.kind IN ('replacement_char', 'non_utf8_likely', 'bom', 'utf16_bom')
                      AND (i.origin IS NULL OR i.severity IS NULL))
                  OR (i.kind = 'bom' AND @is_solution_file = 1)
              )
            LIMIT 1",
            static c =>
            {
                c.Parameters.Add("@path", SqliteType.Text);
                c.Parameters.Add("@is_solution_file", SqliteType.Integer);
            });
        try
        {
            cmd.Parameters["@path"].Value = relativePath;
            cmd.Parameters["@is_solution_file"].Value = isSolutionFile ? 1 : 0;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private bool HasIssueMetadataColumns() =>
        _hasIssueMetadataColumns ??= TableExists("file_issues")
            && ColumnExists("file_issues", "origin")
            && ColumnExists("file_issues", "severity");

    /// <summary>
    /// Check whether the DB currently contains any indexed files for the given language.
    /// 指定言語の indexed file が DB に存在するか確認する。
    /// </summary>
    public bool HasAnyIndexedFiles()
    {
        var cmd = RentCommand("SELECT 1 FROM files LIMIT 1", static _ => { });
        try
        {
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public int GetIndexedFileCount()
    {
        var cmd = RentCommand("SELECT COUNT(*) FROM files", static _ => { });
        try
        {
            var count = Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            return count >= int.MaxValue ? int.MaxValue : (int)count;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public bool HasAnyFilesWithLanguage(string lang)
    {
        LanguagePresenceCheckForTesting?.Invoke(lang);
        var cmd = RentCommand(
            "SELECT 1 FROM files WHERE lang = @lang LIMIT 1",
            static c => c.Parameters.Add("@lang", SqliteType.Text));
        try
        {
            cmd.Parameters["@lang"].Value = lang;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public int CountSymbolsForFile(long fileId)
    {
        var cmd = RentCommand(
            "SELECT COUNT(*) FROM symbols WHERE file_id = @file_id",
            static c => c.Parameters.Add("@file_id", SqliteType.Integer));
        try
        {
            cmd.Parameters["@file_id"].Value = fileId;
            return SqliteCommandPolicy.ReadInt32Scalar(cmd, "symbols count for file");
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public int CountReferencesForFile(long fileId)
    {
        var cmd = RentCommand(
            "SELECT COUNT(*) FROM symbol_references WHERE file_id = @file_id",
            static c => c.Parameters.Add("@file_id", SqliteType.Integer));
        try
        {
            cmd.Parameters["@file_id"].Value = fileId;
            return SqliteCommandPolicy.ReadInt32Scalar(cmd, "symbol reference count for file");
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public bool HasExtractionCapViolationForFile(long fileId, int maxSymbolsPerFile, int maxReferencesPerFile)
        => HasReusableFileBlockingIssueForFile(fileId, maxSymbolsPerFile, maxReferencesPerFile, generatedExtractionSuppressed: null);

    public bool HasReusableFileBlockingIssueForFile(
        long fileId,
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        bool? generatedExtractionSuppressed)
    {
        var cmd = RentCommand(
            @"
            SELECT CASE WHEN
                (SELECT COUNT(*) FROM symbols WHERE file_id = @file_id) > @max_symbols
                OR EXISTS (
                    SELECT 1
                    FROM file_issues
                    WHERE file_id = @file_id
                      AND kind = 'symbol_count_exceeded'
                )
                OR (SELECT COUNT(*) FROM symbol_references WHERE file_id = @file_id) > @max_references
                OR EXISTS (
                    SELECT 1
                    FROM file_issues
                    WHERE file_id = @file_id
                      AND kind = 'reference_count_exceeded'
                )
                OR (
                    @generated_suppressed IS NOT NULL
                    AND (
                        EXISTS (
                            SELECT 1
                            FROM file_issues
                            WHERE file_id = @file_id
                              AND kind = @generated_issue_kind
                        )
                    ) <> @generated_suppressed
                )
            THEN 1 ELSE 0 END",
            static c =>
            {
                c.Parameters.Add("@file_id", SqliteType.Integer);
                c.Parameters.Add("@max_symbols", SqliteType.Integer);
                c.Parameters.Add("@max_references", SqliteType.Integer);
                c.Parameters.Add("@generated_suppressed", SqliteType.Integer);
                c.Parameters.Add("@generated_issue_kind", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@file_id"].Value = fileId;
            cmd.Parameters["@max_symbols"].Value = maxSymbolsPerFile;
            cmd.Parameters["@max_references"].Value = maxReferencesPerFile;
            cmd.Parameters["@generated_suppressed"].Value = generatedExtractionSuppressed.HasValue
                ? (generatedExtractionSuppressed.Value ? 1 : 0)
                : DBNull.Value;
            cmd.Parameters["@generated_issue_kind"].Value = FileIndexer.GeneratedCodeExtractionSkippedIssueKind;
            return SqliteCommandPolicy.ReadInt32Scalar(cmd, "reusable file blocking issue") != 0;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public bool HasIssueForFile(long fileId, string kind)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM file_issues WHERE file_id = @file_id AND kind = @kind LIMIT 1",
            static c =>
            {
                c.Parameters.Add("@file_id", SqliteType.Integer);
                c.Parameters.Add("@kind", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@file_id"].Value = fileId;
            cmd.Parameters["@kind"].Value = kind;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public IReadOnlyList<string> GetIndexedLanguages()
    {
        var languages = new List<string>();
        if (!TableExists("files"))
            return languages;

        IndexedLanguagesReadForTesting?.Invoke();
        var cmd = RentCommand(
            @"
            SELECT DISTINCT f.lang
            FROM files f
            WHERE f.lang IS NOT NULL
              AND f.lang <> ''
              AND EXISTS (SELECT 1 FROM symbols s WHERE s.file_id = f.id)
            ORDER BY f.lang",
            static _ => { });
        try
        {
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                languages.Add(reader.GetString(0));
        }
        finally
        {
            ReleaseCommand(cmd);
        }
        return languages;
    }
}

internal readonly record struct ReusableIndexedFileStat(
    long FileId,
    DateTime ModifiedUtc,
    long Size,
    string Language,
    bool GeneratedExtractionSuppressed);

internal readonly record struct PersistedIndexedFileSize(
    bool Exists,
    bool SizeKnown,
    long Size);

internal sealed class ReusableIndexedFileStatsSnapshot : Dictionary<string, ReusableIndexedFileStat>
{
    private readonly int _reusableCapacity;
    private bool _reusableCapacityApplied;
    private Dictionary<string, long>? _nonReusablePersistedSizes;
    private HashSet<string>? _unknownPersistedSizePaths;

    internal ReusableIndexedFileStatsSnapshot(int capacity)
        : base(0, StringComparer.Ordinal)
    {
        _reusableCapacity = capacity;
    }

    internal void RecordReusable(string path, ReusableIndexedFileStat stat)
    {
        if (!_reusableCapacityApplied)
        {
            EnsureCapacity(_reusableCapacity);
            _reusableCapacityApplied = true;
        }
        this[path] = stat;
    }

    internal void RecordNonReusablePersistedSize(string path, bool sizeKnown, long size)
    {
        if (sizeKnown)
        {
            (_nonReusablePersistedSizes ??= new Dictionary<string, long>(StringComparer.Ordinal))[path] = size;
            return;
        }

        (_unknownPersistedSizePaths ??= new HashSet<string>(StringComparer.Ordinal)).Add(path);
    }

    internal PersistedIndexedFileSize GetPersistedSize(string path)
    {
        if (TryGetValue(path, out var reusable))
            return new PersistedIndexedFileSize(Exists: true, SizeKnown: true, reusable.Size);
        if (_nonReusablePersistedSizes?.TryGetValue(path, out var size) == true)
            return new PersistedIndexedFileSize(Exists: true, SizeKnown: true, size);
        if (_unknownPersistedSizePaths?.Contains(path) == true)
            return new PersistedIndexedFileSize(Exists: true, SizeKnown: false, 0);
        return new PersistedIndexedFileSize(Exists: false, SizeKnown: false, 0);
    }
}
