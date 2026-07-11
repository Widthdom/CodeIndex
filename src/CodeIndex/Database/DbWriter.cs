using Microsoft.Data.Sqlite;
using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

/// <summary>
/// Handles INSERT/UPSERT operations to the database with batch commits.
/// Transaction scopes on a writer are serialized; nested scopes are supported on the
/// owning thread and other threads wait until the active scope is disposed.
/// バッチコミットによるINSERT/UPSERT処理を担当する。
/// writer 上の transaction scope は直列化され、同一所有スレッドのネストのみ許可する。
/// </summary>
public partial class DbWriter
{
    private const string FoldBackfillPhaseMetaKey = "fold_backfill_phase";
    private const string FoldBackfillLastSymbolIdMetaKey = "fold_backfill_last_symbol_id";
    private const string FoldBackfillLastReferenceIdMetaKey = "fold_backfill_last_reference_id";

    public const string FtsIncrementalWritesSinceOptimizeMetaKey = "fts_incremental_writes_since_optimize";
    public const string FtsLastOptimizedAtMetaKey = "fts_last_optimized_at";
    public const string FtsBulkLoadInProgressMetaKey = "fts_bulk_load_in_progress";
    public const int DefaultFtsOptimizeIncrementalWriteThreshold = 25;

    private readonly SqliteConnection _conn;
    private readonly PreparedCommandCache? _commandCache;
    private readonly Action? _markWriteWork;
    private static readonly AsyncLocal<Action?> ScopedFoldBackfillRowUpdatedForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedFoldBackfillVerificationForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedLanguagePresenceCheckForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedIndexedLanguagesReadForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedReusableUnchangedFileLookupForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedCountsReadForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedBatchRowSkipWarningForTesting = new();
    private static readonly AsyncLocal<Action<DbWriterBatchProgress>?> ScopedBatchProgressCheckpointForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedMutualRecursionRefreshForTesting = new();
    internal static Action? FoldBackfillRowUpdatedForTesting
    {
        get => ScopedFoldBackfillRowUpdatedForTesting.Value;
        set => ScopedFoldBackfillRowUpdatedForTesting.Value = value;
    }

    internal static Action? FoldBackfillVerificationForTesting
    {
        get => ScopedFoldBackfillVerificationForTesting.Value;
        set => ScopedFoldBackfillVerificationForTesting.Value = value;
    }

    internal static Action<string>? LanguagePresenceCheckForTesting
    {
        get => ScopedLanguagePresenceCheckForTesting.Value;
        set => ScopedLanguagePresenceCheckForTesting.Value = value;
    }

    internal static Action? IndexedLanguagesReadForTesting
    {
        get => ScopedIndexedLanguagesReadForTesting.Value;
        set => ScopedIndexedLanguagesReadForTesting.Value = value;
    }

    internal static Action<string>? ReusableUnchangedFileLookupForTesting
    {
        get => ScopedReusableUnchangedFileLookupForTesting.Value;
        set => ScopedReusableUnchangedFileLookupForTesting.Value = value;
    }

    internal static Action? CountsReadForTesting
    {
        get => ScopedCountsReadForTesting.Value;
        set => ScopedCountsReadForTesting.Value = value;
    }

    internal static Action<string>? BatchRowSkipWarningForTesting
    {
        get => ScopedBatchRowSkipWarningForTesting.Value;
        set => ScopedBatchRowSkipWarningForTesting.Value = value;
    }

    internal static Action<DbWriterBatchProgress>? BatchProgressCheckpointForTesting
    {
        get => ScopedBatchProgressCheckpointForTesting.Value;
        set => ScopedBatchProgressCheckpointForTesting.Value = value;
    }

    internal static Action? MutualRecursionRefreshForTesting
    {
        get => ScopedMutualRecursionRefreshForTesting.Value;
        set => ScopedMutualRecursionRefreshForTesting.Value = value;
    }

    // Transaction ownership (#4154): the semaphore is held for the outermost writer
    // transaction lifetime. Same-stack nested calls from the owning thread and
    // AsyncLocal token skip the semaphore and become SAVEPOINTs; other flows wait even
    // when ExecutionContext copied the token into Task.Run.
    private readonly object _transactionStateLock = new();
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly AsyncLocal<Guid?> _currentTransactionGateToken = new();
    private static readonly TimeSpan DefaultTransactionStateContentionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TransactionStateContentionWaitInterval = TimeSpan.FromMilliseconds(50);
    private const int BatchSize = 500;
    private const int DeleteFilesBatchSize = 500;
    private const int SqliteConstraintErrorCode = 19;
    private const int TypeScriptModuleSyntaxFallbackMaxBytes = (int)FileIndexer.DefaultMaxFileSizeBytes;
    private const int TypeScriptModuleSyntaxFallbackMaxLines = 16384;
    private static readonly ConcurrentDictionary<int, string> ChunkInsertSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> SymbolInsertSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> ReferenceInsertSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> ReferenceLineUpsertSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> ReferenceLineLookupSqlCache = new();
    private static readonly ConcurrentDictionary<int, string> ReferenceLineInsertSqlCache = new();
    private static readonly BoundedRegex CSharpExternAliasSignatureRegex = new(
        @"^\s*extern\s+alias\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpGlobalUsingSignatureRegex = new(
        @"^\s*global\s+using\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpUsingStaticSignatureRegex = new(
        @"^\s*(?:global\s+)?using\s+static\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly BoundedRegex CSharpUsingAliasSignatureRegex = new(
        @"^\s*(?:global\s+)?using\s+(?<alias>@?\w+)\s*=\s*(?<target>[^;]+?)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private int _rowSkipSavepointCounter;
    private long _batchRowsSkipped;
    private int _transactionDepth;
    private int _transactionOwnerThreadId;
    private Guid _transactionOwnerToken;
    private string? _transactionOwnerOperation;
    private DateTimeOffset _transactionOwnerAcquiredAtUtc;
    private bool? _hasIssueMetadataColumns;
    internal static TimeSpan? TransactionStateContentionTimeoutForTesting { get; set; }
    // Outermost SqliteTransaction currently held open by this writer (null when no
    // transaction is active OR after the outermost transaction has been committed /
    // rolled back). Tracked so cached prepared commands can be re-pointed at the live
    // transaction on every lease — SqliteCommand validates Transaction against the
    // connection's current transaction at execute time and would throw
    // TransactionRequired / TransactionConnectionMismatch after a transaction boundary
    // if we kept a stale reference. Cleared on Commit / Rollback (and at depth-0
    // Dispose as a safety net) so a subsequent lease outside any transaction sets the
    // cached command's Transaction back to null. Issue #1566.
    // 現在 writer が保持している最外 SqliteTransaction。キャッシュ済み prepared command の
    // Transaction を毎回その時点の active transaction に同期させるため保持する。
    // Commit / Rollback / Dispose で必ず null に戻し、トランザクション外で借用したときに
    // cached command の Transaction を null に再同期できるようにする。Issue #1566.
    private SqliteTransaction? _activeTransaction;
    internal SqliteConnection Connection => _conn;
    public long BatchRowsSkipped => Volatile.Read(ref _batchRowsSkipped);

    internal sealed record DbWriterBatchProgress(string Operation, int RowsProcessed, int RowsTotal);

    public DbWriter(SqliteConnection connection)
        : this(connection, commandCache: null, markWriteWork: null)
    {
    }

    /// <summary>
    /// Construct a writer that shares its owning <see cref="DbContext"/>'s
    /// <see cref="PreparedCommandCache"/>. Hot per-file paths (`GetUnchangedFileId`,
    /// `UpsertFile`, file-data cleanup) then reuse one prepared statement per SQL text
    /// instead of constructing a fresh command per call. Issue #1566.
    /// 所属 <see cref="DbContext"/> の <see cref="PreparedCommandCache"/> を共有する writer
    /// を構築する。ファイル単位のホットパスは SQL ごとに 1 つの prepared statement を再利用する。
    /// Issue #1566.
    /// </summary>
    public DbWriter(DbContext context)
        : this(
            (context ?? throw new ArgumentNullException(nameof(context))).Connection,
            context.IsReadOnly ? null : context.PreparedCommands,
            () => context.MarkWriteWork())
    {
    }

    internal DbWriter(SqliteConnection connection, PreparedCommandCache? commandCache, Action? markWriteWork)
    {
        _conn = connection;
        _commandCache = commandCache;
        _markWriteWork = markWriteWork;
    }

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Upsert a metadata key/value into `codeindex_meta`.
    /// codeindex_meta への key/value の upsert。
    /// </summary>
    public void SetMeta(string key, string? value)
    {
        if (!HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            Execute("SAVEPOINT set_meta_atomic");
            try
            {
                SetMetaCore(key, value);
                Execute("RELEASE SAVEPOINT set_meta_atomic");
            }
            catch
            {
                try { Execute("ROLLBACK TO SAVEPOINT set_meta_atomic"); } catch (SqliteException) { /* best effort */ }
                try { Execute("RELEASE SAVEPOINT set_meta_atomic"); } catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        SetMetaCore(key, value);
    }

    private void SetMetaCore(string key, string? value)
    {
        var cmd = RentCommand(
            @"INSERT INTO codeindex_meta (key, value) VALUES (@key, @value)
                            ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            static c =>
            {
                c.Parameters.Add("@key", SqliteType.Text);
                c.Parameters.Add("@value", SqliteType.Text);
            });
        try
        {
            cmd.Parameters["@key"].Value = key;
            cmd.Parameters["@value"].Value = (object?)value ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    public void SetMetaValues(params (string Key, string? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0 || !HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            Execute("SAVEPOINT set_meta_values_atomic");
            try
            {
                SetMetaValuesCore(values);
                Execute("RELEASE SAVEPOINT set_meta_values_atomic");
            }
            catch
            {
                try { Execute("ROLLBACK TO SAVEPOINT set_meta_values_atomic"); } catch (SqliteException) { /* best effort */ }
                try { Execute("RELEASE SAVEPOINT set_meta_values_atomic"); } catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        SetMetaValuesCore(values);
    }

    private void SetMetaValuesCore(IReadOnlyList<(string Key, string? Value)> values)
    {
        var keyParameterNames = new string[values.Count];
        var valueParameterNames = new string[values.Count];
        var rows = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            keyParameterNames[i] = "@meta_key" + suffix;
            valueParameterNames[i] = "@meta_value" + suffix;
            rows[i] = "(" + keyParameterNames[i] + ", " + valueParameterNames[i] + ")";
        }

        var sql = "INSERT INTO codeindex_meta (key, value) VALUES " + string.Join(", ", rows)
            + " ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        var cmd = RentCommand(
            sql,
            c =>
            {
                for (var i = 0; i < keyParameterNames.Length; i++)
                {
                    c.Parameters.Add(keyParameterNames[i], SqliteType.Text);
                    c.Parameters.Add(valueParameterNames[i], SqliteType.Text);
                }
            });
        try
        {
            for (var i = 0; i < values.Count; i++)
            {
                cmd.Parameters[keyParameterNames[i]].Value = values[i].Key;
                cmd.Parameters[valueParameterNames[i]].Value = (object?)values[i].Value ?? DBNull.Value;
            }
            cmd.ExecuteNonQuery();
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    private void ClearMetaKeys(params string[] keys)
    {
        if (keys.Length == 0 || !HasMetaTable())
            return;

        if (!IsInTransaction())
        {
            Execute("SAVEPOINT clear_meta_keys_atomic");
            try
            {
                ClearMetaKeysCore(keys);
                Execute("RELEASE SAVEPOINT clear_meta_keys_atomic");
            }
            catch
            {
                try { Execute("ROLLBACK TO SAVEPOINT clear_meta_keys_atomic"); } catch (SqliteException) { /* best effort */ }
                try { Execute("RELEASE SAVEPOINT clear_meta_keys_atomic"); } catch (SqliteException) { /* best effort */ }
                throw;
            }
            return;
        }

        ClearMetaKeysCore(keys);
    }

    private void ClearMetaKeysCore(IReadOnlyList<string> keys)
    {
        var values = new (string Key, string? Value)[keys.Count];
        for (var i = 0; i < keys.Count; i++)
            values[i] = (keys[i], null);

        SetMetaValuesCore(values);
    }

    private string? GetMetaString(string key)
    {
        var cmd = RentCommand(
            "SELECT value FROM codeindex_meta WHERE key = @key",
            static c => c.Parameters.Add("@key", SqliteType.Text));
        try
        {
            cmd.Parameters["@key"].Value = key;
            return cmd.ExecuteScalar() as string;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }
    public bool HasMetaTable() => TableExists("codeindex_meta");

    private bool TableExists(string name)
    {
        var cmd = RentCommand(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
            static c => c.Parameters.Add("@name", SqliteType.Text));
        try
        {
            cmd.Parameters["@name"].Value = name;
            return cmd.ExecuteScalar() != null;
        }
        finally
        {
            ReleaseCommand(cmd);
        }
    }

    /// <summary>
    /// True only when every existing row in symbols / symbol_references has a populated folded
    /// value for each source name that is itself non-NULL. Callers use this before stamping
    /// `FoldReadyFlag` on a full scan because the default incremental path skips unchanged files
    /// — their pre-#86 rows still carry NULL folded columns, so a naive stamp would flip readers
    /// onto the folded equality path and silently miss those legacy rows. Codex #86 review.
    /// full scan 成功時でも、incremental で skip された legacy 行が NULL のまま残っていれば
    /// fold-ready にしてはならない。stamp 前にこの実検証を通す。
    /// </summary>
    public bool AllFoldedColumnsBackfilled(
        bool requireCurrentSymbolExtractorVersions = false,
        bool requireCurrentFoldKeys = false)
    {
        if (IsInTransaction())
            return AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorVersions, requireCurrentFoldKeys);

        bool ownTransaction = true;
        Execute("BEGIN DEFERRED");
        try
        {
            var result = AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorVersions, requireCurrentFoldKeys);
            Execute("COMMIT");
            ownTransaction = false;
            return result;
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); }
                catch (SqliteException) { /* best effort */ }
            }

            throw;
        }
    }

    private bool AllFoldedColumnsBackfilledCore(
        bool requireCurrentSymbolExtractorVersions,
        bool requireCurrentFoldKeys)
    {
        if (requireCurrentSymbolExtractorVersions && !SymbolExtractorVersionsMatchCurrent())
            return false;

        FoldBackfillVerificationForTesting?.Invoke();
        var cmd = RentCommand(
            @"
            SELECT
                (SELECT COUNT(*) FROM symbols WHERE name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE container_name IS NOT NULL AND container_name_folded IS NULL)",
            static _ => { });
        try
        {
            var raw = cmd.ExecuteScalar();
            long missing = raw is long l ? l : (raw is int i ? i : 0);
            if (missing != 0)
                return false;
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return !requireCurrentFoldKeys || AllFoldedColumnValuesMatchCurrentFold();
    }

    public bool AllFoldedColumnValuesMatchCurrentFold()
    {
        var symbols = RentCommand(
            "SELECT name, name_folded FROM symbols WHERE name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = symbols.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var expected = NameFold.Fold(reader.GetString(0));
                var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    return false;
            }
        }
        finally
        {
            ReleaseCommand(symbols);
        }

        var references = RentCommand(
            @"
                SELECT symbol_name, symbol_name_folded, container_name, container_name_folded
                FROM symbol_references
                WHERE symbol_name IS NOT NULL OR container_name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = references.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (!reader.IsDBNull(0))
                {
                    var expected = NameFold.Fold(reader.GetString(0));
                    var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }

                if (!reader.IsDBNull(2))
                {
                    var expected = NameFold.Fold(reader.GetString(2));
                    var actual = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }
            }
        }
        finally
        {
            ReleaseCommand(references);
        }

        return true;
    }

    public bool AllFoldedColumnsBackfilled(IReadOnlyCollection<string> requireCurrentSymbolExtractorLanguages)
    {
        if (IsInTransaction())
            return AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorLanguages);

        bool ownTransaction = true;
        Execute("BEGIN DEFERRED");
        try
        {
            var result = AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorLanguages);
            Execute("COMMIT");
            ownTransaction = false;
            return result;
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); }
                catch (SqliteException) { /* best effort */ }
            }

            throw;
        }
    }

    private bool AllFoldedColumnsBackfilledCore(IReadOnlyCollection<string> requireCurrentSymbolExtractorLanguages)
    {
        if (requireCurrentSymbolExtractorLanguages.Count > 0
            && !SymbolExtractorVersionsMatchCurrent(requireCurrentSymbolExtractorLanguages))
        {
            return false;
        }

        return AllFoldedColumnsBackfilledCore(
            requireCurrentSymbolExtractorVersions: false,
            requireCurrentFoldKeys: false);
    }

    public bool SymbolExtractorVersionsMatchCurrent()
    {
        foreach (var lang in GetIndexedLanguages())
        {
            if (!SymbolExtractorVersionMatchesCurrent(lang))
                return false;
        }

        return true;
    }

    public bool SymbolExtractorVersionsMatchCurrent(IEnumerable<string> languages)
    {
        foreach (var lang in languages)
        {
            if (!SymbolExtractorVersionMatchesCurrent(lang))
                return false;
        }

        return true;
    }

    private bool SymbolExtractorVersionMatchesCurrent(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return true;

        var stored = GetMetaString(DbContext.GetSymbolExtractorVersionMetaKey(lang));
        if (stored == null)
            return true;

        var current = SymbolExtractor.GetContractVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return stored == current;
    }

    /// <summary>
    /// Recompute persisted folded-name keys from existing symbol / reference rows without
    /// reparsing source files. This is used to upgrade legacy DBs (NULL folded columns) and
    /// to refresh stored keys after a future <see cref="NameFold.Version"/> bump.
    /// ソース再解析なしで既存行から folded key を再計算する。legacy DB の NULL 埋めと、
    /// 将来の <see cref="NameFold.Version"/> 変更時の key 再生成に使う。
    /// </summary>
    /// <param name="rewriteAll">
    /// When true, rewrite every non-null source name even if the folded column is already
    /// populated. Needed when the stored fold metadata does not match the current binary/runtime.
    /// true のとき、既に埋まっている folded 列も含めて全行再計算する（fold metadata 不一致時）。
    /// </param>
    /// <returns>Counts of symbol rows and reference rows rewritten.</returns>
    public (int Symbols, int SymbolReferences) BackfillFoldedColumns(
        bool rewriteAll = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var foldBackfillPhase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var symbols = BackfillSymbolFoldedRows(rewriteAll, cancellationToken);
        if (rewriteAll && foldBackfillPhase != "references")
        {
            SetMeta(FoldBackfillPhaseMetaKey, "references");
            SetMeta(FoldBackfillLastReferenceIdMetaKey, "0");
        }

        var symbolReferences = BackfillReferenceFoldedRows(rewriteAll, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (rewriteAll)
            ClearFoldBackfillCheckpoint();

        return (symbols, symbolReferences);
    }

    public (int Symbols, int SymbolReferences) CountBackfillFoldedColumns(bool rewriteAll = false)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;

        var symbolsSql = rewriteAll && phase != "references"
            ? "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId"
            : rewriteAll
            ? "SELECT 0"
            : "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
        var symbolsUsesCheckpoint = rewriteAll && phase != "references";
        var symbols = RentCommand(
            symbolsSql,
            symbolsUsesCheckpoint
                ? static c => c.Parameters.Add("@lastSymbolId", SqliteType.Integer)
                : static _ => { });

        var referencesSql = rewriteAll
            ? @"SELECT COUNT(*)
                FROM symbol_references
                WHERE id > @lastReferenceId
                  AND (symbol_name IS NOT NULL OR container_name IS NOT NULL)"
            : @"SELECT COUNT(*)
                FROM symbol_references
                WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                   OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
        var references = RentCommand(
            referencesSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastReferenceId", SqliteType.Integer)
                : static _ => { });

        try
        {
            if (symbolsUsesCheckpoint)
                symbols.Parameters["@lastSymbolId"].Value = lastSymbolId;
            if (rewriteAll)
                references.Parameters["@lastReferenceId"].Value = phase == "references" ? lastReferenceId : 0;

            return (ToInt32Count(symbols.ExecuteScalar()), ToInt32Count(references.ExecuteScalar()));
        }
        finally
        {
            ReleaseCommand(references);
            ReleaseCommand(symbols);
        }
    }

    private static int ToInt32Count(object? value)
    {
        var count = value is long l ? l : (value is int i ? i : 0);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private int BackfillSymbolFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        if (phase == "references")
            return 0;

        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var rows = new List<(long Id, string Name)>();
        var selectSql = rewriteAll
            ? "SELECT id, name FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId ORDER BY id"
            : "SELECT id, name FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
        var select = RentCommand(
            selectSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastSymbolId", SqliteType.Integer)
                : static _ => { });
        try
        {
            if (rewriteAll)
                select.Parameters["@lastSymbolId"].Value = lastSymbolId;
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            "UPDATE symbols SET name_folded = @folded WHERE id = @id",
            static c =>
            {
                c.Parameters.Add("@folded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pFolded = update.Parameters["@folded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pFolded.Value = (object?)NameFold.Fold(row.Name) ?? DBNull.Value;
                pId.Value = row.Id;
                update.ExecuteNonQuery();
                if (rewriteAll)
                    SetMeta(FoldBackfillLastSymbolIdMetaKey, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FoldBackfillRowUpdatedForTesting?.Invoke();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }

        return rows.Count;
    }

    private int BackfillReferenceFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;
        var rows = new List<(long Id, string? SymbolName, string? ContainerName)>();
        var selectSql = rewriteAll
            ? @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE id > @lastReferenceId
                      AND (symbol_name IS NOT NULL OR container_name IS NOT NULL)
                    ORDER BY id"
            : @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                       OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
        var select = RentCommand(
            selectSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastReferenceId", SqliteType.Integer)
                : static _ => { });
        try
        {
            if (rewriteAll)
                select.Parameters["@lastReferenceId"].Value = lastReferenceId;
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            @"UPDATE symbol_references
                               SET symbol_name_folded = @symbolNameFolded,
                                   container_name_folded = @containerNameFolded
                               WHERE id = @id",
            static c =>
            {
                c.Parameters.Add("@symbolNameFolded", SqliteType.Text);
                c.Parameters.Add("@containerNameFolded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pSymbolNameFolded = update.Parameters["@symbolNameFolded"];
            var pContainerNameFolded = update.Parameters["@containerNameFolded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pSymbolNameFolded.Value = (object?)NameFold.Fold(row.SymbolName) ?? DBNull.Value;
                pContainerNameFolded.Value = (object?)NameFold.Fold(row.ContainerName) ?? DBNull.Value;
                pId.Value = row.Id;
                update.ExecuteNonQuery();
                if (rewriteAll)
                    SetMeta(FoldBackfillLastReferenceIdMetaKey, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FoldBackfillRowUpdatedForTesting?.Invoke();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }

        return rows.Count;
    }

    private long GetFoldBackfillCheckpoint(string key)
    {
        var value = GetMetaString(key);
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private void ClearFoldBackfillCheckpoint()
    {
        SetMeta(FoldBackfillPhaseMetaKey, null);
        SetMeta(FoldBackfillLastSymbolIdMetaKey, null);
        SetMeta(FoldBackfillLastReferenceIdMetaKey, null);
    }

    private static object FoldedNameDbValue(string? name, Dictionary<string, string?> cache)
    {
        if (name == null)
            return DBNull.Value;

        if (!cache.TryGetValue(name, out var folded))
        {
            folded = NameFold.Fold(name);
            cache[name] = folded;
        }

        return (object?)folded ?? DBNull.Value;
    }

    private static Dictionary<string, string?> CreateFoldedNameCache(int rowCount, int namesPerRow)
    {
        if (rowCount <= 0 || namesPerRow <= 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        var capacity = rowCount > int.MaxValue / namesPerRow
            ? int.MaxValue
            : rowCount * namesPerRow;
        return new Dictionary<string, string?>(capacity, StringComparer.Ordinal);
    }

    private static StringBuilder CreateBatchSqlBuilder(int rowCount, int estimatedCharsPerRow)
    {
        const int BaseCapacity = 256;
        if (rowCount <= 0 || estimatedCharsPerRow <= 0)
            return new StringBuilder(BaseCapacity);

        var rowCapacity = rowCount > (int.MaxValue - BaseCapacity) / estimatedCharsPerRow
            ? int.MaxValue - BaseCapacity
            : rowCount * estimatedCharsPerRow;
        return new StringBuilder(BaseCapacity + rowCapacity);
    }

    private static int GetRowsPerInsertStatement(int columnCount)
    {
        if (columnCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnCount));

        return Math.Max(1, Math.Min(BatchSize, SqliteDynamicSql.MaxSqlVariables / columnCount));
    }

    private bool IsInTransaction() => _transactionDepth > 0;

}
