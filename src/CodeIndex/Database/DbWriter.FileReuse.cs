using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private static readonly AsyncLocal<Action?> ScopedReusableStatSnapshotReadForTesting = new();

    internal static Action? ReusableStatSnapshotReadForTesting
    {
        get => ScopedReusableStatSnapshotReadForTesting.Value;
        set => ScopedReusableStatSnapshotReadForTesting.Value = value;
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
    /// repository 全体の stat-reuse 候補を 1 回で読み、filesystem stat は各 file で再確認する。
    /// </summary>
    internal IReadOnlyDictionary<string, ReusableIndexedFileStat> LoadReusableIndexedFileStats(
        int maxSymbolsPerFile,
        int maxReferencesPerFile,
        CancellationToken cancellationToken = default,
        int initialCapacity = 0)
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
        var cmd = RentCommand(
            $@"SELECT
                    f.id,
                    f.path,
                    f.modified,
                    f.size,
                    f.lang,
                    {generatedSuppressionProjection} AS generated_suppressed
                FROM files f
                WHERE f.lang IS NOT NULL
                  AND typeof(f.modified) = 'text'
                  AND typeof(f.size) = 'integer'
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
                  )",
            c =>
            {
                c.Parameters.Add("@max_symbols", SqliteType.Integer);
                c.Parameters.Add("@max_references", SqliteType.Integer);
                if (hasIssuesTable)
                    c.Parameters.Add("@generated_issue_kind", SqliteType.Text);
            });
        var currentVersionByLanguage = new Dictionary<string, bool>(StringComparer.Ordinal);
        var reusable = new Dictionary<string, ReusableIndexedFileStat>(initialCapacity, StringComparer.Ordinal);
        try
        {
            cmd.Parameters["@max_symbols"].Value = maxSymbolsPerFile;
            cmd.Parameters["@max_references"].Value = maxReferencesPerFile;
            if (hasIssuesTable)
                cmd.Parameters["@generated_issue_kind"].Value = FileIndexer.GeneratedCodeExtractionSkippedIssueKind;
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var language = reader.GetString(4);
                if (!currentVersionByLanguage.TryGetValue(language, out var versionCurrent))
                {
                    versionCurrent = SymbolExtractorVersionMatchesCurrent(language);
                    currentVersionByLanguage.Add(language, versionCurrent);
                }

                if (!versionCurrent
                    || !TryParseStoredModifiedUtc(reader.GetString(2), out var modifiedUtc))
                {
                    continue;
                }

                var path = reader.GetString(1);
                reusable[path] = new ReusableIndexedFileStat(
                    reader.GetInt64(0),
                    modifiedUtc,
                    reader.GetInt64(3),
                    language,
                    reader.GetInt64(5) != 0);
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
        }

        return reusable;
    }

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
