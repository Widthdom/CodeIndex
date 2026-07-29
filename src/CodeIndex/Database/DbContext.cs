using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace CodeIndex.Database;

internal enum ExistingCodeIndexDbValidationFailure
{
    None,
    Missing,
    Inaccessible,
    InvalidTarget,
    InvalidDatabase,
    SchemaTooNew,
    Exception,
}

/// <summary>
/// Manages SQLite connection and schema initialization.
/// SQLite接続とスキーマ初期化を管理する。
/// </summary>
public partial class DbContext : IDisposable
{
    public const int ApplicationId = 0x43444958; // "CDIX"
    public const int DefaultCacheSizeKb = 65536;
    public const int MaxCacheSizeKb = 1048576;
    public const long DefaultMmapSizeBytes = 268435456;
    public const long MaxMmapSizeBytes = 1073741824;
    public const string CacheSizeEnvironmentVariable = "CDIDX_SQLITE_CACHE_KB";
    public const string MmapSizeEnvironmentVariable = "CDIDX_SQLITE_MMAP_BYTES";
    public const string BusyTimeoutEnvironmentVariable = "CDIDX_SQLITE_BUSY_TIMEOUT_MS";
    internal const string DatabaseOpenMissingCategory = "missing_database";
    internal const string DatabaseOpenPermissionCategory = "permission_denied";
    internal const string DatabaseOpenSidecarCategory = "sidecar_failure";
    internal const string DatabaseOpenInvalidUriCategory = "invalid_uri";
    internal const string DatabaseOpenUnknownCategory = "unknown_open_failure";
    private const int SqliteCantOpenDirtyWal = 14 | (5 << 8);
    public const int DefaultWalAutocheckpointPages = 1000;
    public const string DefaultSynchronousMode = "NORMAL";
    public const string SymbolExtractorVersionMetaPrefix = "symbol_extractor_version_";
    internal const string FtsChunksTrigramTableName = "fts_chunks_trigram";
    private const int MigrationDiagnosticTextLimit = 240;
    private const int MigrationForeignKeyViolationSampleLimit = 5;
    internal const string DropFtsChunksInsertTriggerSql = "DROP TRIGGER IF EXISTS fts_chunks_ai";
    internal const string DropFtsChunksDeleteTriggerSql = "DROP TRIGGER IF EXISTS fts_chunks_ad";
    internal const string DropFtsChunksUpdateTriggerSql = "DROP TRIGGER IF EXISTS fts_chunks_au";
    internal const string DropFtsChunksSyncTriggersSql =
        DropFtsChunksInsertTriggerSql + ";\n"
        + DropFtsChunksDeleteTriggerSql + ";\n"
        + DropFtsChunksUpdateTriggerSql;
    internal const string CreateFtsChunksInsertTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS fts_chunks_ai AFTER INSERT ON chunks BEGIN
            INSERT INTO fts_chunks(rowid, content) VALUES (new.id, new.content);
        END
        """;
    internal const string CreateFtsChunksDeleteTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS fts_chunks_ad AFTER DELETE ON chunks BEGIN
            INSERT INTO fts_chunks(fts_chunks, rowid, content) VALUES('delete', old.id, old.content);
        END
        """;
    internal const string CreateFtsChunksUpdateTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS fts_chunks_au AFTER UPDATE ON chunks BEGIN
            INSERT INTO fts_chunks(fts_chunks, rowid, content) VALUES('delete', old.id, old.content);
            INSERT INTO fts_chunks(rowid, content) VALUES (new.id, new.content);
        END
        """;
    internal const string CreateFtsChunksSyncTriggersSql =
        CreateFtsChunksInsertTriggerSql + ";\n"
        + CreateFtsChunksDeleteTriggerSql + ";\n"
        + CreateFtsChunksUpdateTriggerSql;
    internal const string DropFtsChunksTrigramInsertTriggerSql = "DROP TRIGGER IF EXISTS fts_chunks_trigram_ai";
    internal const string DropFtsChunksTrigramDeleteTriggerSql = "DROP TRIGGER IF EXISTS fts_chunks_trigram_ad";
    internal const string DropFtsChunksTrigramUpdateTriggerSql = "DROP TRIGGER IF EXISTS fts_chunks_trigram_au";
    internal const string DropFtsChunksTrigramSyncTriggersSql =
        DropFtsChunksTrigramInsertTriggerSql + ";\n"
        + DropFtsChunksTrigramDeleteTriggerSql + ";\n"
        + DropFtsChunksTrigramUpdateTriggerSql;
    internal const string DropAllFtsChunksSyncTriggersSql =
        DropFtsChunksSyncTriggersSql + ";\n"
        + DropFtsChunksTrigramSyncTriggersSql;
    internal const string CreateFtsChunksTrigramInsertTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS fts_chunks_trigram_ai AFTER INSERT ON chunks BEGIN
            INSERT INTO fts_chunks_trigram(rowid, content) VALUES (new.id, new.content);
        END
        """;
    internal const string CreateFtsChunksTrigramDeleteTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS fts_chunks_trigram_ad AFTER DELETE ON chunks BEGIN
            INSERT INTO fts_chunks_trigram(fts_chunks_trigram, rowid, content) VALUES('delete', old.id, old.content);
        END
        """;
    internal const string CreateFtsChunksTrigramUpdateTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS fts_chunks_trigram_au AFTER UPDATE ON chunks BEGIN
            INSERT INTO fts_chunks_trigram(fts_chunks_trigram, rowid, content) VALUES('delete', old.id, old.content);
            INSERT INTO fts_chunks_trigram(rowid, content) VALUES (new.id, new.content);
        END
        """;
    internal const string CreateFtsChunksTrigramSyncTriggersSql =
        CreateFtsChunksTrigramInsertTriggerSql + ";\n"
        + CreateFtsChunksTrigramDeleteTriggerSql + ";\n"
        + CreateFtsChunksTrigramUpdateTriggerSql;
    internal const string CountFtsChunksTrigramSyncTriggersSql = """
        SELECT COUNT(*)
        FROM sqlite_master
        WHERE type = 'trigger'
          AND name IN (
              'fts_chunks_trigram_ai',
              'fts_chunks_trigram_ad',
              'fts_chunks_trigram_au')
        """;
    internal const string CreateAllFtsChunksSyncTriggersSql =
        CreateFtsChunksSyncTriggersSql + ";\n"
        + CreateFtsChunksTrigramSyncTriggersSql;
    internal const string ResourceListGenerationMetaKey = "resource_list_generation";
    private const string EnsureResourceListGenerationSql = """
        INSERT INTO codeindex_meta(key, value)
        VALUES ('resource_list_generation', '0')
        ON CONFLICT(key) DO NOTHING
        """;
    private const string IncrementResourceListGenerationSql = """
        INSERT INTO codeindex_meta(key, value)
        VALUES ('resource_list_generation', '1')
        ON CONFLICT(key) DO UPDATE SET
            value = CAST(COALESCE(CAST(value AS INTEGER), 0) + 1 AS TEXT)
        """;
    private const string CreateResourceListGenerationInsertTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS files_resource_generation_ai AFTER INSERT ON files BEGIN
            INSERT INTO codeindex_meta(key, value)
            VALUES ('resource_list_generation', '1')
            ON CONFLICT(key) DO UPDATE SET
                value = CAST(COALESCE(CAST(value AS INTEGER), 0) + 1 AS TEXT);
        END
        """;
    private const string CreateResourceListGenerationDeleteTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS files_resource_generation_ad AFTER DELETE ON files BEGIN
            INSERT INTO codeindex_meta(key, value)
            VALUES ('resource_list_generation', '1')
            ON CONFLICT(key) DO UPDATE SET
                value = CAST(COALESCE(CAST(value AS INTEGER), 0) + 1 AS TEXT);
        END
        """;
    private const string CreateResourceListGenerationUpdateTriggerSql = """
        CREATE TRIGGER IF NOT EXISTS files_resource_generation_au AFTER UPDATE ON files BEGIN
            INSERT INTO codeindex_meta(key, value)
            VALUES ('resource_list_generation', '1')
            ON CONFLICT(key) DO UPDATE SET
                value = CAST(COALESCE(CAST(value AS INTEGER), 0) + 1 AS TEXT);
        END
        """;

    private static readonly string[] RequiredCodeIndexTables =
    [
        "files",
        "chunks",
        "symbols",
    ];
    private static readonly string[] ReadMigrationRequiredTables =
    [
        "reference_lines",
        "symbol_references",
        HotspotReferenceAggregateSql.TableName,
        "symbol_reference_candidates",
        "file_issues",
        "codeindex_meta",
    ];
    private static readonly (string Table, string Column)[] ReadMigrationRequiredColumns =
    [
        ("symbol_references", "reference_line_id"),
        ("symbol_references", "is_self_reference"),
        ("symbol_references", "is_mutual_recursion"),
        ("symbol_references", "symbol_name_folded"),
        ("symbol_references", "container_name_folded"),
        ("files", "lang"),
        ("symbol_references", "source_symbol_id"),
        ("symbol_references", "target_symbol_id"),
        ("symbol_references", "target_symbol_key"),
        ("symbol_references", "target_qualifier"),
        ("symbol_references", "resolution_state"),
        ("symbol_references", "resolution_candidate_count"),
        ("files", "checksum"),
        ("files", "modified"),
        ("files", "indexed_at"),
        ("symbols", "start_line"),
        ("symbols", "end_line"),
        ("symbols", "body_start_line"),
        ("symbols", "body_end_line"),
        ("symbols", "signature"),
        ("symbols", "container_kind"),
        ("symbols", "container_name"),
        ("symbols", "container_qualified_name"),
        ("symbols", "family_key"),
        ("symbols", "visibility"),
        ("symbols", "return_type"),
        ("symbols", "is_metadata_target"),
        ("symbols", "metadata_target_source"),
        ("symbols", "name_folded"),
        ("symbols", "display_name_folded"),
    ];
    private static readonly string[] ReadMigrationRequiredIndexes =
    [
        "idx_chunks_file_end_start_nonnull",
        "idx_chunks_file_start_chunk_nonnull",
        "idx_symbol_refs_name",
        "idx_symbol_refs_file",
        "idx_symbol_refs_container",
        "idx_symbol_refs_container_kind",
        "idx_symbol_refs_name_kind",
        "idx_symbol_refs_name_file",
        "idx_reference_lines_file_line",
        "idx_symbol_refs_reference_line",
        "idx_symbol_refs_name_nocase",
        "idx_symbol_refs_container_nocase",
        "idx_symbol_refs_name_nocase_kind",
        "idx_symbol_refs_name_nocase_file",
        "idx_symbol_refs_container_nocase_kind",
        "idx_symbols_name_nocase",
        "idx_symbols_name_folded",
        "idx_symbols_display_name_folded",
        "idx_symbols_file_name_folded",
        "idx_symbols_file_name_nocase",
        "idx_symbols_name_folded_container_name_nocase",
        "idx_symbols_name_folded_container_qualified_name_nocase",
        "idx_symbol_refs_symbol_name_folded",
        "idx_symbol_refs_container_name_folded",
        "idx_symbol_refs_symbol_name_folded_kind",
        "idx_symbol_refs_symbol_name_folded_file",
        "idx_symbol_refs_container_name_folded_kind",
        "idx_hotspot_reference_counts_global",
        "idx_hotspot_reference_counts_file",
        "idx_hotspot_reference_counts_leaf",
        "idx_hotspot_reference_counts_rank",
        "idx_symbol_refs_source_symbol",
        "idx_symbol_refs_target_symbol",
        "idx_symbol_refs_resolved_source_target_kind",
        "idx_symbol_ref_candidates_symbol",
    ];
    internal static readonly string[] ResourceListGenerationTriggerNames =
    [
        "files_resource_generation_ai",
        "files_resource_generation_ad",
        "files_resource_generation_au",
    ];

    private SqliteConnection _connection = null!;
    private bool _isReadOnly;
    private bool _readOnlyFallback;
    private bool _walCheckpointAttempted;
    private bool _walCheckpointSucceeded;
    private bool _readOnlyImmutableFallback;
    private bool _immutableReadOnly;
    private bool _immutableReadOnlyWalRisk;
    private bool _connectionPooling = true;
    private bool _queryOnlySnapshotRequiresRefresh;
    private string? _queryOnlySnapshotSourcePath;
    private DbConnectionFactory.QueryOnlySnapshotSourceState? _queryOnlySnapshotSourceState;
    private string? _walCheckpointSkippedReason;
    private string? _walCheckpointFailureReason;
    private long? _walCheckpointBusy;
    private long? _walCheckpointLogPageCount;
    private long? _walCheckpointCheckpointedPageCount;
    private long? _walCheckpointRemainingPageCount;
    private readonly string? _schemaCacheKey;
    private SqliteTransaction? _activeMigrationTransaction;
    private MigrationTransactionOwnership _migrationTransactionOwnership;
    private readonly object _schemaCacheLock = new();
    private DbSchemaCache? _schemaCache;
    private bool _disposed;
    private PreparedCommandCache? _preparedCommands;
    private bool _suppressWriteWorkTracking = true;
    private bool _hasWriteWork;
    private bool _suppressPlannerStatisticsMaintenanceOnClose;
    private bool _hasWalCheckpointableWriteWork;
    private bool _rebuildFtsAfterSchemaMigration;
    private bool _rebuildTrigramFtsAfterSchemaMigration;
    private readonly DbOpenIntent _openIntent;
    private readonly DatabasePermissionPolicyMode _databasePermissionPolicy;
    private readonly IDatabaseFileModeProvider _databaseFileModeProvider;
    private readonly CancellationToken _cancellation;
    private readonly List<StatusDatabasePermissionDiagnostic> _databasePermissionDiagnostics = [];

    private static readonly AsyncLocal<Action<string>?> ScopedOptimizePragmaExecutedForTesting = new();
    private static readonly AsyncLocal<Action<SqliteCommand>?> ScopedPlannerStatisticsCommandCreatedForTesting = new();
    private static readonly AsyncLocal<Action<string, string>?> ScopedPlannerStatisticsCommandExecutedForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedWalCheckpointTruncateExecutedForTesting = new();
    private static readonly AsyncLocal<Action<string, string>?> ScopedMaintenanceProgressForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedForeignKeysDisabledForTesting = new();
    private static readonly AsyncLocal<Action<string, long>?> ScopedForeignKeysRestoringForTesting = new();
    private static readonly AsyncLocal<Action<SqliteConnection, string>?> ScopedForeignKeyValidationBeforeCheckForTesting = new();
    private static readonly AsyncLocal<Func<SqliteConnection, SqliteTransaction>?> ScopedReadMigrationTransactionFactoryForTesting = new();

    private enum MigrationTransactionOwnership
    {
        None,
        Owned,
        External,
    }

    internal static Action<string>? OptimizePragmaExecutedForTesting
    {
        get => ScopedOptimizePragmaExecutedForTesting.Value;
        set => ScopedOptimizePragmaExecutedForTesting.Value = value;
    }

    internal static Action<SqliteCommand>? PlannerStatisticsCommandCreatedForTesting
    {
        get => ScopedPlannerStatisticsCommandCreatedForTesting.Value;
        set => ScopedPlannerStatisticsCommandCreatedForTesting.Value = value;
    }

    internal static Action<string, string>? PlannerStatisticsCommandExecutedForTesting
    {
        get => ScopedPlannerStatisticsCommandExecutedForTesting.Value;
        set => ScopedPlannerStatisticsCommandExecutedForTesting.Value = value;
    }

    internal static Action<string>? WalCheckpointTruncateExecutedForTesting
    {
        get => ScopedWalCheckpointTruncateExecutedForTesting.Value;
        set => ScopedWalCheckpointTruncateExecutedForTesting.Value = value;
    }

    internal static Action<string, string>? MaintenanceProgressForTesting
    {
        get => ScopedMaintenanceProgressForTesting.Value;
        set => ScopedMaintenanceProgressForTesting.Value = value;
    }

    internal static Action<string>? ForeignKeysDisabledForTesting
    {
        get => ScopedForeignKeysDisabledForTesting.Value;
        set => ScopedForeignKeysDisabledForTesting.Value = value;
    }

    internal static Action<string, long>? ForeignKeysRestoringForTesting
    {
        get => ScopedForeignKeysRestoringForTesting.Value;
        set => ScopedForeignKeysRestoringForTesting.Value = value;
    }

    internal static Action<SqliteConnection, string>? ForeignKeyValidationBeforeCheckForTesting
    {
        get => ScopedForeignKeyValidationBeforeCheckForTesting.Value;
        set => ScopedForeignKeyValidationBeforeCheckForTesting.Value = value;
    }

    internal static Func<SqliteConnection, SqliteTransaction>? ReadMigrationTransactionFactoryForTesting
    {
        get => ScopedReadMigrationTransactionFactoryForTesting.Value;
        set => ScopedReadMigrationTransactionFactoryForTesting.Value = value;
    }

    public SqliteConnection Connection => _connection;
    public DbOpenIntent OpenIntent => _openIntent;
    public bool IsReadOnly => _isReadOnly;
    public bool ReadOnlyFallback => _readOnlyFallback;
    public bool WalCheckpointAttempted => _walCheckpointAttempted;
    public bool WalCheckpointSucceeded => _walCheckpointSucceeded;
    public bool ReadOnlyImmutableFallback => _readOnlyImmutableFallback;
    internal bool ImmutableReadOnly => _immutableReadOnly;
    internal bool ImmutableReadOnlyWalRisk => _immutableReadOnlyWalRisk;
    internal bool ConnectionPooling => _connectionPooling;
    internal bool QueryOnlySnapshotRequiresRefresh => _queryOnlySnapshotRequiresRefresh;
    internal bool IsQueryOnlySnapshotCurrent(CancellationToken cancellationToken = default)
        => !_queryOnlySnapshotRequiresRefresh
           || (_queryOnlySnapshotSourcePath != null
               && _queryOnlySnapshotSourceState is { } state
               && DbConnectionFactory.IsQueryOnlySnapshotCurrent(
                   _queryOnlySnapshotSourcePath,
                   state,
                   cancellationToken));
    public string? WalCheckpointSkippedReason => _walCheckpointSkippedReason;
    public string? WalCheckpointFailureReason => _walCheckpointFailureReason;
    public long? WalCheckpointBusy => _walCheckpointBusy;
    public long? WalCheckpointLogPageCount => _walCheckpointLogPageCount;
    public long? WalCheckpointCheckpointedPageCount => _walCheckpointCheckpointedPageCount;
    public long? WalCheckpointRemainingPageCount => _walCheckpointRemainingPageCount;
    public WalCheckpointResult LastWalCheckpointResult => new(
        _walCheckpointAttempted,
        _walCheckpointSucceeded,
        _walCheckpointBusy,
        _walCheckpointLogPageCount,
        _walCheckpointCheckpointedPageCount,
        _walCheckpointRemainingPageCount,
        _walCheckpointSkippedReason,
        _walCheckpointFailureReason);
    public string DatabasePermissionPolicyName => DatabasePermissionPolicy.ToName(_databasePermissionPolicy);
    public IReadOnlyList<StatusDatabasePermissionDiagnostic> DatabasePermissionDiagnostics => _databasePermissionDiagnostics;

    public static string GetSymbolExtractorVersionMetaKey(string lang)
        => SymbolExtractorVersionMetaPrefix + lang;

    /// <summary>
    /// DB-path-scoped schema cache. Created lazily so a `DbContext` that
    /// never opens a reader pays nothing. Subsequent `DbReader` instances for
    /// the same database reuse cached `PRAGMA table_info` / `PRAGMA index_list`
    /// / `sqlite_master` results instead of re-running the scan on every
    /// construction (issues #1565 / #1701).
    /// </summary>
    public DbSchemaCache SchemaCache
    {
        get
        {
            lock (_schemaCacheLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(DbContext));
                return _schemaCache ??= new DbSchemaCache(_connection, _schemaCacheKey);
            }
        }
    }

    /// <summary>
    /// Drop cached schema state so subsequent reads observe DDL that ran
    /// outside this `DbContext`. Migrations performed by this instance
    /// (`InitializeSchema`, `TryMigrateForRead`, `DropAll`) already invalidate
    /// the cache automatically.
    /// </summary>
    public void RefreshSchemaCache()
    {
        lock (_schemaCacheLock)
        {
            if (!_disposed)
                _schemaCache?.Refresh();
        }
    }

    /// <summary>
    /// Lazily-initialized LRU cache of prepared <see cref="SqliteCommand"/> instances shared
    /// by hot read/write paths (e.g. <see cref="DbWriter"/>'s per-file lookups). Issue #1566.
    /// ホットパス共有の prepared command LRU キャッシュ。Issue #1566.
    /// </summary>
    internal PreparedCommandCache PreparedCommands
        => _preparedCommands ??= new PreparedCommandCache(
            _connection,
            PreparedCommandCache.ReadCapacityFromEnvironment());

    public static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        out string message,
        out bool isNotFound,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            requireWritable: true,
            requireSupportedUserVersion: false,
            out message,
            out isNotFound,
            out _,
            cancellationToken);

    internal static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        bool requireWritable,
        bool requireSupportedUserVersion,
        out string message,
        out bool isNotFound,
        out bool isSchemaTooNew,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            requireWritable,
            requireSupportedUserVersion,
            out message,
            out isNotFound,
            out isSchemaTooNew,
            out _,
            out _,
            cancellationToken);

    internal static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        bool requireWritable,
        bool requireSupportedUserVersion,
        out string message,
        out bool isNotFound,
        out bool isSchemaTooNew,
        out ExistingCodeIndexDbValidationFailure validationFailure,
        out Exception? validationException,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(dbPath, openTarget =>
        {
            var mode = requireWritable ? SqliteConnectionPolicyMode.ReadWrite : SqliteConnectionPolicyMode.ReadOnly;
            return new SqliteConnection(SqliteConnectionPolicy.BuildConnectionString(openTarget, mode));
        },
        static connection => connection.Open(),
        null,
        requireWritable,
        requireSupportedUserVersion,
        out message,
        out isNotFound,
        out isSchemaTooNew,
        out validationFailure,
        out validationException,
        cancellationToken);

    internal static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        Func<string, SqliteConnection> createConnection,
        Action<SqliteConnection> openConnection,
        Action<int>? sleep,
        out string message,
        out bool isNotFound,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            createConnection,
            openConnection,
            sleep,
            requireWritable: true,
            requireSupportedUserVersion: false,
            out message,
            out isNotFound,
            out _,
            out _,
            out _,
            cancellationToken);

    internal static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        Func<string, SqliteConnection> createConnection,
        Action<SqliteConnection> openConnection,
        Action<int>? sleep,
        out string message,
        out bool isNotFound,
        out Exception? validationException,
        CancellationToken cancellationToken = default)
        => TryValidateExistingCodeIndexDb(
            dbPath,
            createConnection,
            openConnection,
            sleep,
            requireWritable: true,
            requireSupportedUserVersion: false,
            out message,
            out isNotFound,
            out _,
            out _,
            out validationException,
            cancellationToken);

    private static bool TryValidateExistingCodeIndexDb(
        string dbPath,
        Func<string, SqliteConnection> createConnection,
        Action<SqliteConnection> openConnection,
        Action<int>? sleep,
        bool requireWritable,
        bool requireSupportedUserVersion,
        out string message,
        out bool isNotFound,
        out bool isSchemaTooNew,
        out ExistingCodeIndexDbValidationFailure validationFailure,
        out Exception? validationException,
        CancellationToken cancellationToken = default)
    {
        message = string.Empty;
        isNotFound = false;
        isSchemaTooNew = false;
        validationFailure = ExistingCodeIndexDbValidationFailure.None;
        validationException = null;
        cancellationToken.ThrowIfCancellationRequested();

        if (SqliteFileUri.StartsWithFileScheme(dbPath) && !SqliteFileUri.TryValidateBounds(dbPath, out var boundsError))
        {
            validationFailure = ExistingCodeIndexDbValidationFailure.InvalidTarget;
            message = FormatDatabaseOpenFailure(
                DatabaseOpenInvalidUriCategory,
                dbPath,
                boundsError?.Message ?? "Invalid SQLite file URI.");
            return false;
        }

        if (requireWritable && SqliteFileUri.StartsWithFileScheme(dbPath) && SqliteFileUri.RequestsReadOnly(dbPath))
        {
            validationFailure = ExistingCodeIndexDbValidationFailure.Inaccessible;
            message = $"database must be writable: {dbPath}";
            return false;
        }

        var openTarget = dbPath;
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            if (!TryGetLocalPath(dbPath, out var normalized, out var pathFailureReason)
                || normalized == null)
            {
                validationFailure = ExistingCodeIndexDbValidationFailure.InvalidTarget;
                message = FormatDatabaseOpenFailure(
                    DatabaseOpenInvalidUriCategory,
                    dbPath,
                    pathFailureReason);
                return false;
            }

            openTarget = normalized;
        }

        var preflight = ProbeDatabasePath(openTarget);
        if (preflight is DatabasePathProbe.Missing or DatabasePathProbe.PermissionDenied or DatabasePathProbe.Directory)
        {
            var category = preflight switch
            {
                DatabasePathProbe.Missing => DatabaseOpenMissingCategory,
                DatabasePathProbe.PermissionDenied => DatabaseOpenPermissionCategory,
                _ => DatabaseOpenUnknownCategory,
            };
            message = FormatDatabaseOpenFailure(category, dbPath);
            isNotFound = category == DatabaseOpenMissingCategory;
            validationFailure = preflight switch
            {
                DatabasePathProbe.Missing => ExistingCodeIndexDbValidationFailure.Missing,
                DatabasePathProbe.PermissionDenied => ExistingCodeIndexDbValidationFailure.Inaccessible,
                _ => ExistingCodeIndexDbValidationFailure.InvalidTarget,
            };
            return false;
        }

        try
        {
            using var connection = requireWritable
                ? OpenSqliteConnectionWithRetry(
                    () => createConnection(openTarget),
                    openConnection,
                    sleep,
                    dbPath: dbPath,
                    cancellationToken: cancellationToken)
                : OpenArtifactPreservingQueryOnly(dbPath);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = SqliteCommandPolicy.PragmaSql("application_id");
            if (SqliteCommandPolicy.ReadInt64Scalar(cmd, "pragma application_id") != ApplicationId)
            {
                validationFailure = ExistingCodeIndexDbValidationFailure.InvalidDatabase;
                message = $"database is not an existing CodeIndex DB: {dbPath}";
                return false;
            }

            if (requireSupportedUserVersion)
            {
                cmd.CommandText = SqliteCommandPolicy.PragmaSql("user_version");
                var userVersion = SqliteCommandPolicy.ReadInt32Scalar(cmd, "pragma user_version");
                var unknownBits = userVersion & ~CurrentSchemaVersion;
                if (unknownBits != 0)
                {
                    isSchemaTooNew = true;
                    validationFailure = ExistingCodeIndexDbValidationFailure.SchemaTooNew;
                    message = $"database was written by a newer cdidx schema stamp (user_version {userVersion}); this binary supports up to {CurrentSchemaVersion}: {dbPath}";
                    return false;
                }
            }

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = cmd.ExecuteReader();
            var tables = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
                tables.Add(reader.GetString(0));

            if (RequiredCodeIndexTables.All(tables.Contains))
                return true;

            validationFailure = ExistingCodeIndexDbValidationFailure.InvalidDatabase;
            message = $"database is not an existing CodeIndex DB: {dbPath}";
            return false;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 14)
        {
            validationFailure = ExistingCodeIndexDbValidationFailure.Exception;
            validationException = ex;
            var category = ClassifyCantOpenFailure(openTarget, ex.SqliteExtendedErrorCode);
            message = FormatDatabaseOpenFailure(category, dbPath);
            isNotFound = category == DatabaseOpenMissingCategory;
            return false;
        }
        catch (SqliteException ex)
        {
            validationFailure = ExistingCodeIndexDbValidationFailure.Exception;
            validationException = ex;
            message = $"database is not an existing CodeIndex DB: {dbPath}";
            return false;
        }
        catch (CodeIndexException ex)
        {
            validationFailure = ExistingCodeIndexDbValidationFailure.Exception;
            validationException = ex;
            message = ex.Message;
            return false;
        }
    }

    internal static string ClassifyCantOpenFailure(string dbPath, int sqliteExtendedErrorCode)
    {
        if (sqliteExtendedErrorCode == SqliteCantOpenDirtyWal)
            return DatabaseOpenSidecarCategory;

        return ProbeDatabasePath(dbPath) switch
        {
            DatabasePathProbe.Missing => DatabaseOpenMissingCategory,
            DatabasePathProbe.PermissionDenied => DatabaseOpenPermissionCategory,
            _ when HasInaccessibleSqliteSidecar(dbPath) => DatabaseOpenSidecarCategory,
            _ => DatabaseOpenUnknownCategory,
        };
    }

    private static bool HasInaccessibleSqliteSidecar(string dbPath)
        => ProbeDatabasePath(dbPath + "-wal") == DatabasePathProbe.PermissionDenied
           || ProbeDatabasePath(dbPath + "-shm") == DatabasePathProbe.PermissionDenied;

    private static DatabasePathProbe ProbeDatabasePath(string path)
    {
        try
        {
            var attributes = File.GetAttributes(LongPath.EnsureWindowsPrefix(path));
            if ((attributes & FileAttributes.Directory) != 0)
                return DatabasePathProbe.Directory;

            using var stream = new FileStream(
                LongPath.EnsureWindowsPrefix(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.RandomAccess);
            return DatabasePathProbe.Readable;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return DatabasePathProbe.Missing;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return DatabasePathProbe.PermissionDenied;
        }
        catch (IOException)
        {
            return DatabasePathProbe.Unknown;
        }
    }

    private static string FormatDatabaseOpenFailure(string category, string dbPath, string? detail = null)
    {
        var displayPath = dbPath;
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            var queryIndex = dbPath.IndexOf('?', StringComparison.Ordinal);
            if (queryIndex >= 0)
                displayPath = dbPath[..queryIndex];
        }
        var pathLabel = DiagnosticSanitizer.ForPath(displayPath);
        var sanitizedDetail = string.IsNullOrWhiteSpace(detail)
            ? null
            : DiagnosticSanitizer.ForMessage(detail);
        var prefix = category == DatabaseOpenMissingCategory
            ? $"database not found [{category}]"
            : $"database open failed [{category}]";
        return sanitizedDetail == null
            ? $"{prefix}: {pathLabel}"
            : $"{prefix}: {pathLabel}; {sanitizedDetail}";
    }

    private enum DatabasePathProbe
    {
        Readable,
        Missing,
        PermissionDenied,
        Directory,
        Unknown,
    }

    public DbContext(DbOpenIntent openIntent, string dbPath, CancellationToken cancellationToken = default)
        : this(
            openIntent,
            dbPath,
            DatabasePermissionPolicy.Resolve(),
            SystemDatabaseFileModeProvider.Instance,
            cancellationToken)
    {
    }

    internal DbContext(
        DbOpenIntent openIntent,
        string dbPath,
        DatabasePermissionPolicyMode databasePermissionPolicy,
        IDatabaseFileModeProvider databaseFileModeProvider,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(openIntent))
            throw new ArgumentOutOfRangeException(nameof(openIntent), openIntent, "Unknown database open intent.");

        _databasePermissionPolicy = databasePermissionPolicy;
        _databaseFileModeProvider = databaseFileModeProvider ?? throw new ArgumentNullException(nameof(databaseFileModeProvider));
        _cancellation = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        _openIntent = openIntent;
        _schemaCacheKey = TryCreateSchemaCacheKey(dbPath);

        if (openIntent == DbOpenIntent.QueryOnly)
        {
            OpenQueryOnly(dbPath, cancellationToken);
            _suppressWriteWorkTracking = false;
            return;
        }

        // Write-capable intents reject explicitly read-only URIs. Bare file URIs are
        // normalized to filesystem paths before entering the writable-open path.
        // write-capable intent では明示 read-only URI を拒否し、bare file URI は
        // filesystem path に正規化してから writable open へ進む。
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            if (!SqliteFileUri.TryValidateBounds(dbPath, out var boundsError))
                throw boundsError ?? new FormatException("Invalid SQLite file URI.");

            if (SqliteFileUri.RequestsReadOnly(dbPath))
            {
                throw new InvalidOperationException(
                    $"Database open intent '{openIntent}' requires a writable SQLite path; use {nameof(DbOpenIntent)}.{nameof(DbOpenIntent.QueryOnly)} for a read-only URI.");
            }

            // Bare file: URI — normalize to a filesystem path and fall through.
            // immutable/mode=ro 指定のない file: URI はローカルパスに戻して通常経路で開く。
            if (TryGetLocalPath(dbPath, out var normalized, out var pathFailureReason)
                && normalized != null)
            {
                dbPath = normalized;
                _schemaCacheKey = TryCreateSchemaCacheKey(dbPath);
            }
            else
            {
                _walCheckpointSkippedReason = pathFailureReason;
            }
        }

        // Route through the shared SQLite connection policy so mode/pooling/timeout
        // assumptions stay consistent across CLI and MCP callers.
        // mode/pooling/timeout の前提を CLI/MCP で揃えるため共有ポリシーを通す。
        var connectionString = SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.Default);

        try
        {
            _connection = OpenSqliteConnectionWithRetry(
                () => new SqliteConnection(connectionString),
                static connection => connection.Open(),
                dbPath: dbPath,
                cancellationToken: cancellationToken);
            ApplyBusyTimeoutPragma();
            ApplyConnectionPerformancePragmas();
            RegisterConnectionFunctionsWithRetry(_connection, cancellationToken: cancellationToken);
            EnsureWritableUserVersionSupported(dbPath);
            ConfigureAutoVacuumForEmptyDatabase();
            Execute($"PRAGMA application_id={ApplicationId}");
            ApplyPrivateDatabaseFileModes(dbPath);

            // Enable WAL mode and verify it was applied / WALモードを有効にし適用を確認
            var journalMode = ExecuteScalar("PRAGMA journal_mode=WAL");
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                CommandErrorWriter.WriteStderr($"Warning: WAL mode not enabled (got '{journalMode}')");
            ExecuteSynchronousPragmaWithFallback(Execute);
            Execute(DbPragmaPolicy.WalAutocheckpointPragmaSql(DefaultWalAutocheckpointPages));
            ApplyPrivateDatabaseFileModes(dbPath);
            Execute("PRAGMA optimize=0x10002");
            WarnIfBatchInProgress();
        }
        catch (SqliteException ex) when (IsReadOnlyOpenError(ex, dbPath))
        {
            // Retry as read-only so indexes living on read-only filesystems / WORM storage /
            // sandbox mounts still drive the degraded read path (no WAL, no migration, no writes).
            // This automatic path keeps WAL visibility by using Mode=ReadOnly only. Callers must
            // explicitly provide an immutable=1 file URI if they accept a potentially stale base
            // database snapshot when sidecars cannot be opened.
            // read-only FS / サンドボックスでも縮退 read path を動かせるようフォールバック。
            // 自動経路は Mode=ReadOnly のみとし、immutable=1 は stale risk を受け入れる明示指定に限る。
            _connection?.Dispose();
            var checkpointResult = _walCheckpointSkippedReason == null
                ? CheckpointWalBeforeReadOnlyFallback(dbPath, cancellationToken)
                : WalCheckpointResult.SkippedAfterAttempt(_walCheckpointSkippedReason);
            ApplyWalCheckpointResult(checkpointResult);
            if (_walCheckpointSucceeded)
            {
                try
                {
                    _connection = OpenSqliteConnectionWithRetry(
                        () => new SqliteConnection(connectionString),
                        static connection => connection.Open(),
                        dbPath: dbPath,
                        cancellationToken: cancellationToken);
                    ApplyBusyTimeoutPragma();
                    ApplyConnectionPerformancePragmas();
                    RegisterConnectionFunctionsWithRetry(_connection, cancellationToken: cancellationToken);
                    EnsureWritableUserVersionSupported(dbPath);
                    ConfigureAutoVacuumForEmptyDatabase();
                    Execute($"PRAGMA application_id={ApplicationId}");
                    ApplyPrivateDatabaseFileModes(dbPath);
                    var journalMode = ExecuteScalar("PRAGMA journal_mode=WAL");
                    if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
                        CommandErrorWriter.WriteStderr($"Warning: WAL mode not enabled (got '{journalMode}')");
                    ExecuteSynchronousPragmaWithFallback(Execute);
                    Execute(DbPragmaPolicy.WalAutocheckpointPragmaSql(DefaultWalAutocheckpointPages));
                    ApplyPrivateDatabaseFileModes(dbPath);
                    Execute("PRAGMA optimize=0x10002");
                    WarnIfBatchInProgress();
                }
                catch (SqliteException retryEx) when (IsReadOnlyOpenError(retryEx, dbPath))
                {
                    _connection?.Dispose();
                    _readOnlyFallback = true;
                    OpenReadOnlyFallback(dbPath, cancellationToken);
                }

                if (!_isReadOnly)
                    EnsureForeignKeysEnabled();
                _suppressWriteWorkTracking = false;
                return;
            }

            try
            {
                _readOnlyFallback = true;
                OpenReadOnlyFallback(dbPath, cancellationToken);
            }
            catch
            {
                _connection?.Dispose();
                throw;
            }
        }
        catch
        {
            _connection?.Dispose();
            throw;
        }

        if (!_isReadOnly)
        {
            EnsureForeignKeysEnabled();
        }

        _suppressWriteWorkTracking = false;
    }

}
