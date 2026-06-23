using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace CodeIndex.Database;

/// <summary>
/// Manages SQLite connection and schema initialization.
/// SQLite接続とスキーマ初期化を管理する。
/// </summary>
public class DbContext : IDisposable
{
    public const int ApplicationId = 0x43444958; // "CDIX"
    public const int DefaultCacheSizeKb = 65536;
    public const int MaxCacheSizeKb = 1048576;
    public const long DefaultMmapSizeBytes = 268435456;
    public const long MaxMmapSizeBytes = 1073741824;
    public const string CacheSizeEnvironmentVariable = "CDIDX_SQLITE_CACHE_KB";
    public const string MmapSizeEnvironmentVariable = "CDIDX_SQLITE_MMAP_BYTES";
    public const string BusyTimeoutEnvironmentVariable = "CDIDX_SQLITE_BUSY_TIMEOUT_MS";
    public const int DefaultWalAutocheckpointPages = 1000;
    public const string DefaultSynchronousMode = "NORMAL";
    public const string SymbolExtractorVersionMetaPrefix = "symbol_extractor_version_";
    private const int MigrationDiagnosticTextLimit = 240;
    private const int MigrationForeignKeyViolationSampleLimit = 5;

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
    ];
    private static readonly string[] ReadMigrationRequiredIndexes =
    [
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
        "idx_symbol_refs_symbol_name_folded",
        "idx_symbol_refs_container_name_folded",
        "idx_symbol_refs_symbol_name_folded_kind",
        "idx_symbol_refs_symbol_name_folded_file",
        "idx_symbol_refs_container_name_folded_kind",
    ];

    private SqliteConnection _connection = null!;
    private bool _isReadOnly;
    private bool _readOnlyFallback;
    private bool _walCheckpointAttempted;
    private bool _walCheckpointSucceeded;
    private bool _readOnlyImmutableFallback;
    private string? _walCheckpointSkippedReason;
    private string? _walCheckpointFailureReason;
    private readonly string? _schemaCacheKey;
    private SqliteTransaction? _activeMigrationTransaction;
    private bool _readMigrationInsideExternalTransaction;
    private DbSchemaCache? _schemaCache;
    private PreparedCommandCache? _preparedCommands;
    private bool _suppressWriteWorkTracking = true;
    private bool _hasWriteWork;
    private bool _hasWalCheckpointableWriteWork;
    private bool _rebuildFtsAfterSchemaMigration;

    private static readonly AsyncLocal<Action<string>?> ScopedOptimizePragmaExecutedForTesting = new();
    private static readonly AsyncLocal<Action<SqliteCommand>?> ScopedPlannerStatisticsCommandCreatedForTesting = new();
    private static readonly AsyncLocal<Action<string, string>?> ScopedPlannerStatisticsCommandExecutedForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedWalCheckpointTruncateExecutedForTesting = new();
    private static readonly AsyncLocal<Action<string, string>?> ScopedMaintenanceProgressForTesting = new();
    private static readonly AsyncLocal<Action<string>?> ScopedForeignKeysDisabledForTesting = new();
    private static readonly AsyncLocal<Action<string, long>?> ScopedForeignKeysRestoringForTesting = new();
    private static readonly AsyncLocal<Action<SqliteConnection, string>?> ScopedForeignKeyValidationBeforeCheckForTesting = new();

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

    public SqliteConnection Connection => _connection;
    public bool IsReadOnly => _isReadOnly;
    public bool ReadOnlyFallback => _readOnlyFallback;
    public bool WalCheckpointAttempted => _walCheckpointAttempted;
    public bool WalCheckpointSucceeded => _walCheckpointSucceeded;
    public bool ReadOnlyImmutableFallback => _readOnlyImmutableFallback;
    public string? WalCheckpointSkippedReason => _walCheckpointSkippedReason;
    public string? WalCheckpointFailureReason => _walCheckpointFailureReason;

    public static string GetSymbolExtractorVersionMetaKey(string lang)
        => SymbolExtractorVersionMetaPrefix + lang;

    /// <summary>
    /// DB-path-scoped schema cache. Created lazily so a `DbContext` that
    /// never opens a reader pays nothing. Subsequent `DbReader` instances for
    /// the same database reuse cached `PRAGMA table_info` / `PRAGMA index_list`
    /// / `sqlite_master` results instead of re-running the scan on every
    /// construction (issues #1565 / #1701).
    /// </summary>
    public DbSchemaCache SchemaCache => _schemaCache ??= new DbSchemaCache(_connection, _schemaCacheKey);

    /// <summary>
    /// Drop cached schema state so subsequent reads observe DDL that ran
    /// outside this `DbContext`. Migrations performed by this instance
    /// (`InitializeSchema`, `TryMigrateForRead`, `DropAll`) already invalidate
    /// the cache automatically.
    /// </summary>
    public void RefreshSchemaCache() => _schemaCache?.Refresh();

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
        CancellationToken cancellationToken = default)
    {
        message = string.Empty;
        isNotFound = false;
        isSchemaTooNew = false;
        cancellationToken.ThrowIfCancellationRequested();

        if (SqliteFileUri.StartsWithFileScheme(dbPath) && !SqliteFileUri.TryValidateBounds(dbPath, out var boundsError))
        {
            message = boundsError?.Message ?? "Invalid SQLite file URI.";
            return false;
        }

        if (requireWritable && SqliteFileUri.StartsWithFileScheme(dbPath) && SqliteFileUri.RequestsReadOnly(dbPath))
        {
            message = $"database must be writable: {dbPath}";
            return false;
        }

        var openTarget = dbPath;
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            var normalized = TryGetLocalPath(dbPath);
            if (normalized != null)
            {
                if (!File.Exists(LongPath.EnsureWindowsPrefix(normalized)))
                {
                    message = $"database not found: {dbPath}";
                    isNotFound = true;
                    return false;
                }

                openTarget = normalized;
            }
        }
        else if (!File.Exists(LongPath.EnsureWindowsPrefix(dbPath)))
        {
            message = $"database not found: {dbPath}";
            isNotFound = true;
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
                : OpenReadOnly(openTarget);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = SqliteCommandPolicy.PragmaSql("application_id");
            if (SqliteCommandPolicy.ReadInt64Scalar(cmd, "pragma application_id") != ApplicationId)
            {
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

            message = $"database is not an existing CodeIndex DB: {dbPath}";
            return false;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 14)
        {
            message = $"database not found: {dbPath}";
            isNotFound = true;
            return false;
        }
        catch (SqliteException)
        {
            message = $"database is not an existing CodeIndex DB: {dbPath}";
            return false;
        }
    }

    public DbContext(string dbPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _schemaCacheKey = TryCreateSchemaCacheKey(dbPath);

        // Explicit URI form (file:///abs/path?immutable=1 etc.) — the user has opted into
        // a read-only open with SQLite-specific URI flags. Skip the writable-open attempt
        // and all write-oriented pragmas. This is the CLI escape hatch for sandboxes where
        // even SqliteOpenMode.ReadOnly cannot touch -shm/-wal side files.
        // URI 形式が渡された場合は writable open を省き、直接 read-only として扱う。
        if (SqliteFileUri.StartsWithFileScheme(dbPath))
        {
            if (!SqliteFileUri.TryValidateBounds(dbPath, out var boundsError))
                throw boundsError ?? new FormatException("Invalid SQLite file URI.");

            // URI escape hatch — but ONLY when the caller explicitly requested read-only
            // semantics. A bare `file:///path.db` without `immutable=1` / `mode=ro` falls
            // through to the normal filesystem path, otherwise SQLite's default open mode
            // is read-write-CREATE and a read command like `status` would silently create
            // or mutate a database. For read-only URIs we open the connection directly
            // and skip TryMigrateForRead / writable pragmas.
            // 明示的に read-only を要求した URI のみエスケープハッチ扱い。裸の file: URI は
            // 通常経路にフォールバックさせて read-write-CREATE の副作用を防ぐ。
            if (SqliteFileUri.RequestsReadOnly(dbPath))
            {
                try
                {
                    _connection = new SqliteConnection(SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadOnly));
                    cancellationToken.ThrowIfCancellationRequested();
                    _connection.Open();
                    ApplyBusyTimeoutPragma();
                    ApplyConnectionPerformancePragmas();
                    RegisterConnectionFunctionsWithRetry(_connection, cancellationToken: cancellationToken);
                    _isReadOnly = true;
                    WarnIfBatchInProgress();
                    return;
                }
                catch
                {
                    _connection?.Dispose();
                    throw;
                }
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
            Execute($"PRAGMA wal_autocheckpoint={DefaultWalAutocheckpointPages}");
            ApplyPrivateDatabaseFileModes(dbPath);
            Execute("PRAGMA optimize=0x10002");
            WarnIfBatchInProgress();
        }
        catch (SqliteException ex) when (IsReadOnlyOpenError(ex))
        {
            // Retry as read-only so indexes living on read-only filesystems / WORM storage /
            // sandbox mounts still drive the degraded read path (no WAL, no migration, no writes).
            // The immutable=1 URI flag is the crucial second step: without it, SQLite still tries
            // to read/lock the -shm/-wal side files and may fail with CANTOPEN on a sandbox that
            // allows reading the DB but nothing else in the directory. Immutable tells SQLite the
            // file will never change, bypassing all journal/wal machinery.
            // read-only FS / サンドボックスでも縮退 read path を動かせるようフォールバック。
            // immutable=1 を付けないと SQLite は -shm/-wal を触ろうとして CANTOPEN で落ちることがある。
            _connection?.Dispose();
            _walCheckpointAttempted = true;
            _walCheckpointSucceeded = _walCheckpointSkippedReason == null
                && TryCheckpointWalBeforeReadOnlyFallback(dbPath, cancellationToken, out _walCheckpointFailureReason);
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
                    Execute($"PRAGMA wal_autocheckpoint={DefaultWalAutocheckpointPages}");
                    ApplyPrivateDatabaseFileModes(dbPath);
                    Execute("PRAGMA optimize=0x10002");
                    WarnIfBatchInProgress();
                }
                catch (SqliteException retryEx) when (IsReadOnlyOpenError(retryEx))
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

    private void OpenReadOnlyFallback(string dbPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connection = OpenReadOnly(dbPath, out _readOnlyImmutableFallback);
        ApplyBusyTimeoutPragma();
        ApplyConnectionPerformancePragmas();
        RegisterConnectionFunctionsWithRetry(_connection, cancellationToken: cancellationToken);
        _isReadOnly = true;
        WarnIfBatchInProgress();
    }

    private static bool TryCheckpointWalBeforeReadOnlyFallback(
        string dbPath,
        CancellationToken cancellationToken,
        out string? failureReason)
    {
        failureReason = null;
        try
        {
            var connectionString = SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadWrite);
            using var connection = OpenSqliteConnectionWithRetry(
                () => new SqliteConnection(connectionString),
                static connection => connection.Open(),
                maxOpenAttempts: 1,
                dbPath: dbPath,
                cancellationToken: cancellationToken);
            using var cmd = SqliteConnectionPolicy.CreateCommand(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            cancellationToken.ThrowIfCancellationRequested();
            ReportMaintenanceProgress("wal_checkpoint", "truncate_start", dbPath);
            cmd.ExecuteNonQuery();
            ReportMaintenanceProgress("wal_checkpoint", "truncate_complete", dbPath);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or CodeIndexException)
        {
            failureReason = FormatWalCheckpointFailureReason(ex);
            return false;
        }
    }

    private static string FormatWalCheckpointFailureReason(Exception ex) => ex switch
    {
        SqliteException sqlite => $"sqlite_error_{sqlite.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)}",
        CodeIndexException codeIndexException => codeIndexException.Code,
        _ => "wal_checkpoint_failed",
    };

    public bool TryCheckpointWalTruncate()
        => TryCheckpointWalTruncate(CancellationToken.None);

    public bool TryCheckpointWalTruncate(CancellationToken cancellationToken)
    {
        if (_isReadOnly)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        _walCheckpointAttempted = true;
        try
        {
            using var cmd = SqliteConnectionPolicy.CreateCommand(_connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            WalCheckpointTruncateExecutedForTesting?.Invoke(_connection.DataSource);
            ReportMaintenanceProgress("wal_checkpoint", "truncate_start", _connection.DataSource);
            cmd.ExecuteNonQuery();
            ReportMaintenanceProgress("wal_checkpoint", "truncate_complete", _connection.DataSource);
            cancellationToken.ThrowIfCancellationRequested();
            _walCheckpointSucceeded = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            _walCheckpointSucceeded = false;
            throw;
        }
        catch (Exception)
        {
            _walCheckpointSucceeded = false;
            return false;
        }
    }

    public static string ToReadOnlyUri(string dbPath)
        => SqliteConnectionPolicy.ToReadOnlyUri(dbPath);

    private static void ApplyPrivateDatabaseFileModes(string dbPath)
    {
        if (OperatingSystem.IsWindows() || dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return;

        ApplyPrivateFileModeIfExists(dbPath);
        ApplyPrivateFileModeIfExists(dbPath + "-wal");
        ApplyPrivateFileModeIfExists(dbPath + "-shm");
    }

    private static void ApplyPrivateFileModeIfExists(string path)
    {
        var normalizedPath = LongPath.EnsureWindowsPrefix(path);
        if (!File.Exists(normalizedPath))
            return;

#pragma warning disable CA1416
        File.SetUnixFileMode(normalizedPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
    }

    public static string? GetUnixFileModeString(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            OperatingSystem.IsWindows() ||
            path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
        {
            return null;
        }

        var mode = File.GetUnixFileMode(path) &
            (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
             UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        return Convert.ToString((int)mode, 8).PadLeft(4, '0');
    }

    private static string? TryCreateSchemaCacheKey(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            return null;

        if (dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var localPath = TryGetLocalPath(dbPath);
            if (localPath == null)
                return null;
            dbPath = localPath;
        }

        try
        {
            return Path.GetFullPath(dbPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void WarnIfBatchInProgress()
    {
        var raw = GetMetaString(BatchInProgressMetaKey);
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
        {
            CommandErrorWriter.WriteStderr("Warning: Last batch did not complete; run `cdidx index --rebuild` to re-index from a known clean state.");
            if (!_isReadOnly)
                Execute("PRAGMA user_version = 0");
        }
    }

    private void ApplyConnectionPerformancePragmas()
    {
        var settings = DbPragmaPolicy.ReadConnectionPragmaSettings(
            CacheSizeEnvironmentVariable,
            DefaultCacheSizeKb,
            MaxCacheSizeKb,
            MmapSizeEnvironmentVariable,
            DefaultMmapSizeBytes,
            MaxMmapSizeBytes,
            Environment.Is64BitProcess);
        DbPragmaPolicy.ApplyConnectionPerformancePragmas(Execute, settings);
    }

    private void ConfigureAutoVacuumForEmptyDatabase()
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
        var objectCount = SqliteCommandPolicy.ReadInt64Scalar(cmd, "sqlite_master object count");
        if (objectCount == 0)
            Execute("PRAGMA auto_vacuum=INCREMENTAL");
    }

    public VacuumResult RunIncrementalVacuum(bool dryRun = false)
        => RunIncrementalVacuum(dryRun, CancellationToken.None);

    public VacuumResult RunIncrementalVacuum(bool dryRun, CancellationToken cancellationToken)
    {
        if (_isReadOnly && !dryRun)
        {
            throw new CodeIndexException(
                code: CommandErrorCodes.DbNotWritable,
                category: CodeIndexExceptionCategory.Database,
                message: "database must be writable for vacuum",
                path: _connection.DataSource,
                hint: "Copy the database to writable storage or rerun cdidx without a read-only --db URI.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReportMaintenanceProgress("vacuum", "metrics_before", _connection.DataSource);
        var before = ReadVacuumMetrics();
        cancellationToken.ThrowIfCancellationRequested();
        if (!dryRun && before.AutoVacuumMode == 2)
        {
            ReportMaintenanceProgress("vacuum", "incremental_vacuum", _connection.DataSource);
            Execute($"PRAGMA incremental_vacuum({before.FreelistCount})");
        }
        else if (!dryRun)
        {
            ReportMaintenanceProgress("vacuum", "enable_incremental_autovacuum", _connection.DataSource);
            Execute("PRAGMA auto_vacuum=INCREMENTAL");
            cancellationToken.ThrowIfCancellationRequested();
            ReportMaintenanceProgress("vacuum", "vacuum_rebuild", _connection.DataSource);
            Execute("VACUUM");
        }
        cancellationToken.ThrowIfCancellationRequested();
        ReportMaintenanceProgress("vacuum", "metrics_after", _connection.DataSource);
        var after = dryRun ? before : ReadVacuumMetrics();
        cancellationToken.ThrowIfCancellationRequested();
        var pagesReclaimed = dryRun ? 0 : Math.Max(0, before.PageCount - after.PageCount);
        var bytesReclaimed = pagesReclaimed * after.PageSize;
        var estimatedPagesReclaimable = Math.Max(0, before.FreelistCount);
        var estimatedBytesReclaimable = estimatedPagesReclaimable * before.PageSize;
        var guidance = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
            after.PageCount,
            after.FreelistCount,
            after.PageSize,
            after.WalSizeBytes,
            after.DbSizeBytes,
            after.AutoVacuumMode));
        return new VacuumResult(
            Status: dryRun ? "dry_run" : "ok",
            DryRun: dryRun,
            PageSize: after.PageSize,
            PageCountBefore: before.PageCount,
            FreelistCountBefore: before.FreelistCount,
            PageCountAfter: after.PageCount,
            FreelistCountAfter: after.FreelistCount,
            PagesReclaimed: pagesReclaimed,
            BytesReclaimed: bytesReclaimed,
            EstimatedPagesReclaimable: estimatedPagesReclaimable,
            EstimatedBytesReclaimable: estimatedBytesReclaimable,
            DbSizeBytesBefore: before.DbSizeBytes,
            WalSizeBytesBefore: before.WalSizeBytes,
            DbSizeBytesAfter: after.DbSizeBytes,
            WalSizeBytesAfter: after.WalSizeBytes,
            AutoVacuumModeBefore: before.AutoVacuumMode,
            AutoVacuumModeBeforeName: MaintenanceGuidanceBuilder.FormatAutoVacuumMode(before.AutoVacuumMode) ?? "unknown",
            AutoVacuumModeAfter: after.AutoVacuumMode,
            AutoVacuumModeAfterName: MaintenanceGuidanceBuilder.FormatAutoVacuumMode(after.AutoVacuumMode) ?? "unknown",
            MaintenanceGuidance: guidance);
    }

    private static void ReportMaintenanceProgress(string operation, string phase, string dbPath)
    {
        GlobalToolLog.Info($"db_maintenance_progress operation={operation} phase={phase} db_path={ConsoleUi.FormatBoundedValue(dbPath)}");
        MaintenanceProgressForTesting?.Invoke(operation, phase);
    }

    private VacuumMetrics ReadVacuumMetrics()
        => new(
            ReadPragmaLong("page_count"),
            ReadPragmaLong("freelist_count"),
            ReadPragmaLong("page_size"),
            ReadAutoVacuumMode(),
            TryGetDatabaseFileSize(),
            TryGetWalFileSize());

    private long ReadAutoVacuumMode() => ReadPragmaLong("auto_vacuum");

    private void ApplyBusyTimeoutPragma()
    {
        var busyTimeoutMs = DbPragmaPolicy.ReadBusyTimeoutMs(BusyTimeoutEnvironmentVariable);
        Execute($"PRAGMA busy_timeout={busyTimeoutMs}");
    }

    private long? TryGetDatabaseFileSize()
    {
        var path = _connection.DataSource;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private long? TryGetWalFileSize()
    {
        var path = _connection.DataSource;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var info = new FileInfo(path + "-wal");
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private long ReadPragmaLong(string name)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = SqliteCommandPolicy.PragmaSql(name);
        return SqliteCommandPolicy.ReadInt64Scalar(cmd, $"pragma {name}");
    }

    private readonly record struct VacuumMetrics(
        long PageCount,
        long FreelistCount,
        long PageSize,
        long AutoVacuumMode,
        long? DbSizeBytes,
        long? WalSizeBytes);

    private void EnsureWritableUserVersionSupported(string dbPath)
    {
        var userVersion = GetUserVersion();
        var unknownBits = userVersion & ~CurrentSchemaVersion;
        if (unknownBits == 0)
            return;

        _connection.Dispose();
        throw new CodeIndexException(
            code: CommandErrorCodes.SchemaTooNew,
            category: CodeIndexExceptionCategory.Database,
            message: $"This DB was written by a newer cdidx schema stamp (user_version {userVersion}); this binary supports up to {CurrentSchemaVersion}.",
            path: dbPath,
            hint: "Run with a current cdidx binary or rebuild the index with this version before writing to the database.");
    }

    internal static void ExecuteSynchronousPragmaWithFallback(Action<string> execute)
        => DbPragmaPolicy.ExecuteSynchronousPragmaWithFallback(execute, DefaultSynchronousMode);

    internal static bool IsSafetyLevelTransactionError(SqliteException ex) =>
        DbPragmaPolicy.IsSafetyLevelTransactionError(ex);

    private static bool IsReadOnlyOpenError(SqliteException ex) =>
        DbConnectionFactory.IsReadOnlyOpenError(ex);

    internal static SqliteConnection OpenSqliteConnectionWithRetry(
        Func<SqliteConnection> createConnection,
        Action<SqliteConnection> openConnection,
        Action<int>? sleep = null,
        int maxOpenAttempts = 5,
        string? dbPath = null,
        CancellationToken cancellationToken = default)
        => DbConnectionFactory.OpenWithRetry(
            createConnection,
            openConnection,
            sleep,
            maxOpenAttempts,
            dbPath,
            cancellationToken);

    private static string? TryGetLocalPath(string uriText)
        => DbConnectionFactory.TryGetLocalPath(uriText);

    private static bool TryGetLocalPath(string uriText, out string? localPath, out string? failureReason)
        => DbConnectionFactory.TryGetLocalPath(uriText, out localPath, out failureReason);

    private static SqliteConnection OpenReadOnly(string dbPath)
        => DbConnectionFactory.OpenReadOnly(dbPath);

    private static SqliteConnection OpenReadOnly(string dbPath, out bool usedImmutableFallback)
        => DbConnectionFactory.OpenReadOnly(dbPath, out usedImmutableFallback);

    internal static void RegisterConnectionFunctions(SqliteConnection connection)
    {
        static int? ToNullableInt(long? value)
            => value is null || value < int.MinValue || value > int.MaxValue ? null : (int)value.Value;

        connection.CreateFunction(
            "sql_leaf_name",
            (string? name) => string.IsNullOrWhiteSpace(name) ? null : SqlNameResolver.GetLeafName(name));
        connection.CreateFunction(
            "sql_leaf_name_folded",
            (string? name) =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                var leafName = SqlNameResolver.GetLeafName(name);
                return leafName.Length == 0 ? null : NameFold.Fold(leafName) ?? leafName;
            });
        connection.CreateFunction(
            "sql_normalize_name",
            (string? name) => string.IsNullOrWhiteSpace(name) ? null : SqlNameResolver.NormalizeQualifiedName(name));
        connection.CreateFunction(
            "sql_normalize_name_folded",
            (string? name) =>
            {
                if (string.IsNullOrWhiteSpace(name))
                    return null;

                var normalizedName = SqlNameResolver.NormalizeQualifiedName(name);
                return normalizedName.Length == 0 ? null : NameFold.Fold(normalizedName) ?? normalizedName;
            });
        connection.CreateFunction(
            "sql_normalize_csharp_verbatim_name",
            (string? text) => string.IsNullOrWhiteSpace(text) ? null : CSharpVerbatimNameNormalizer.Normalize(text));
        connection.CreateFunction(
            "csharp_identifier_occurrence_count",
            (string? text, string? identifier) => CountCSharpIdentifierOccurrences(text, identifier));
        connection.CreateFunction(
            "sql_normalize_exact_source_name",
            (string? text, string? lang) => string.IsNullOrWhiteSpace(text) ? null : ExactSourceSearchNormalizer.Normalize(text, lang));
        connection.CreateFunction(
            "sql_segment_count",
            (string? name) => string.IsNullOrWhiteSpace(name) ? (int?)null : SqlNameResolver.GetSegmentCount(name));
        connection.CreateFunction(
            "sql_context_has_name",
            (string? context, string? query) => SqlNameResolver.ContextContainsQualifiedName(context, query) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_has_name_folded",
            (string? context, string? query) => SqlNameResolver.ContextContainsQualifiedNameFolded(context, query) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_has_name_at",
            (string? context, string? query, long? columnNumber) =>
                SqlNameResolver.ContextContainsQualifiedNameAtColumn(context, query, ToNullableInt(columnNumber)) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_has_name_folded_at",
            (string? context, string? query, long? columnNumber) =>
                SqlNameResolver.ContextContainsQualifiedNameFoldedAtColumn(context, query, ToNullableInt(columnNumber)) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_like_name_at",
            (string? context, string? query, long? columnNumber) =>
                SqlNameResolver.ContextContainsQualifiedNameLikeAtColumn(context, query, ToNullableInt(columnNumber)) ? 1 : 0);
        connection.CreateFunction(
            "sql_context_like_name_folded_at",
            (string? context, string? query, long? columnNumber) =>
                SqlNameResolver.ContextContainsQualifiedNameLikeFoldedAtColumn(context, query, ToNullableInt(columnNumber)) ? 1 : 0);
        connection.CreateFunction(
            "sql_resolve_reference_name",
            (string? symbolName, string? context, string? containerName) =>
            {
                var resolved = SqlNameResolver.ResolveReferenceName(symbolName, context, containerName);
                return resolved.Length == 0 ? null : resolved;
            });
        connection.CreateFunction(
            "sql_resolve_reference_name_folded",
            (string? symbolName, string? context, string? containerName) =>
            {
                var resolved = SqlNameResolver.ResolveReferenceNameFolded(symbolName, context, containerName);
                return resolved.Length == 0 ? null : resolved;
            });
        connection.CreateFunction(
            "sql_resolve_reference_name_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber) =>
            {
                var resolved = SqlNameResolver.ResolveReferenceNameAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber));
                return resolved.Length == 0 ? null : resolved;
            });
        connection.CreateFunction(
            "sql_resolve_reference_name_folded_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber) =>
            {
                var resolved = SqlNameResolver.ResolveReferenceNameFoldedAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber));
                return resolved.Length == 0 ? null : resolved;
            });
        connection.CreateFunction(
            "sql_resolve_reference_segment_count_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber) => (int?)(
                SqlNameResolver.ResolveReferenceSegmentCountAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber)) is var segmentCount
                && segmentCount > 0
                    ? segmentCount
                    : null));
        connection.CreateFunction(
            "sql_reference_matches_target_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber, string? targetName) =>
                SqlNameResolver.ReferenceMatchesTargetAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber), targetName) ? 1 : 0);
        connection.CreateFunction(
            "sql_allow_leaf_fallback_at",
            (string? symbolName, string? context, string? containerName, long? columnNumber) =>
                SqlNameResolver.AllowLeafFallbackAtColumn(symbolName, context, containerName, ToNullableInt(columnNumber)) ? 1 : 0);
    }

    internal static int CountCSharpIdentifierOccurrences(string? text, string? identifier)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(identifier))
            return 0;

        text = MaskCSharpCommentsAndStrings(text);
        var count = 0;
        var searchIndex = 0;
        while (searchIndex < text.Length)
        {
            var index = text.IndexOf(identifier, searchIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            var beforeIndex = index - 1;
            var afterIndex = index + identifier.Length;
            var hasIdentifierBefore = beforeIndex >= 0 && IsCSharpIdentifierPart(text[beforeIndex]);
            var hasIdentifierAfter = afterIndex < text.Length && IsCSharpIdentifierPart(text[afterIndex]);
            if (!hasIdentifierBefore && !hasIdentifierAfter)
                count++;

            searchIndex = index + identifier.Length;
        }

        return count;
    }

    internal static bool HasCSharpIdentifierOccurrenceOutsideLineRange(string? text, string? identifier, int startLine, int endLine)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(identifier))
            return false;

        var normalizedStartLine = Math.Max(1, startLine);
        var normalizedEndLine = Math.Max(normalizedStartLine, endLine);
        text = MaskCSharpCommentsAndStrings(text);

        var inRangeOccurrences = 0;
        var lineNumber = 1;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = text.Length;

            var lineOccurrences = CountCSharpIdentifierOccurrencesInRange(text, identifier, lineStart, lineEnd);
            if (lineOccurrences > 0)
            {
                if (lineNumber < normalizedStartLine || lineNumber > normalizedEndLine)
                    return true;

                inRangeOccurrences += lineOccurrences;
                if (inRangeOccurrences > 1)
                    return true;
            }

            if (lineEnd == text.Length)
                break;

            lineStart = lineEnd + 1;
            lineNumber++;
        }

        return false;
    }

    private static int CountCSharpIdentifierOccurrencesInRange(string text, string identifier, int start, int end)
    {
        var count = 0;
        var searchIndex = start;
        while (searchIndex < end)
        {
            var index = text.IndexOf(identifier, searchIndex, end - searchIndex, StringComparison.Ordinal);
            if (index < 0)
                break;

            var beforeIndex = index - 1;
            var afterIndex = index + identifier.Length;
            var hasIdentifierBefore = beforeIndex >= start && IsCSharpIdentifierPart(text[beforeIndex]);
            var hasIdentifierAfter = afterIndex < end && IsCSharpIdentifierPart(text[afterIndex]);
            if (!hasIdentifierBefore && !hasIdentifierAfter)
                count++;

            searchIndex = index + identifier.Length;
        }

        return count;
    }

    private static bool IsCSharpIdentifierPart(char ch)
    {
        return ch == '_' || char.IsLetterOrDigit(ch);
    }

    private static string MaskCSharpCommentsAndStrings(string text)
    {
        var chars = text.ToCharArray();
        var inBlockComment = false;
        var inLineComment = false;
        var inString = false;
        var inChar = false;
        var inVerbatimString = false;

        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            var next = i + 1 < chars.Length ? chars[i + 1] : '\0';

            if (inLineComment)
            {
                if (ch is '\r' or '\n')
                    inLineComment = false;
                else
                    chars[i] = ' ';
                continue;
            }

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    inBlockComment = false;
                }
                else if (ch is not ('\r' or '\n'))
                {
                    chars[i] = ' ';
                }
                continue;
            }

            if (inString)
            {
                if (ch == '\\' && !inVerbatimString && next != '\0')
                {
                    chars[i] = ' ';
                    if (next is not ('\r' or '\n'))
                        chars[i + 1] = ' ';
                    i++;
                    continue;
                }

                if (inVerbatimString && ch == '"' && next == '"')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    continue;
                }

                if (ch == '"')
                    inString = false;

                chars[i] = ch is '\r' or '\n' ? ch : ' ';
                continue;
            }

            if (inChar)
            {
                if (ch == '\\' && next != '\0')
                {
                    chars[i] = ' ';
                    if (next is not ('\r' or '\n'))
                        chars[i + 1] = ' ';
                    i++;
                    continue;
                }

                if (ch == '\'')
                    inChar = false;

                chars[i] = ch is '\r' or '\n' ? ch : ' ';
                continue;
            }

            if (ch == '/' && next == '/')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                inLineComment = true;
                continue;
            }

            if (ch == '/' && next == '*')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                inBlockComment = true;
                continue;
            }

            if (TryMaskCSharpRawString(chars, ref i))
                continue;

            if (TryMaskCSharpInterpolatedString(chars, ref i))
                continue;

            if (ch == '@' && next == '"')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                inString = true;
                inVerbatimString = true;
                continue;
            }

            if (ch == '"')
            {
                chars[i] = ' ';
                inString = true;
                inVerbatimString = false;
                continue;
            }

            if (ch == '\'')
            {
                chars[i] = ' ';
                inChar = true;
            }
        }

        return new string(chars);
    }

    private static bool TryMaskCSharpRawString(char[] chars, ref int index)
    {
        var start = index;
        var cursor = start;
        while (cursor < chars.Length && chars[cursor] == '$')
            cursor++;

        if (cursor + 2 >= chars.Length
            || chars[cursor] != '"'
            || chars[cursor + 1] != '"'
            || chars[cursor + 2] != '"')
        {
            return false;
        }

        var quoteCount = 0;
        while (cursor + quoteCount < chars.Length && chars[cursor + quoteCount] == '"')
            quoteCount++;
        if (quoteCount < 3)
            return false;

        var interpolationDollarCount = cursor - start;
        MaskRangePreservingNewLines(chars, start, cursor + quoteCount);
        var search = cursor + quoteCount;
        var interpolationBraceDepth = 0;
        while (search < chars.Length)
        {
            if (interpolationBraceDepth == 0 && HasQuoteRun(chars, search, quoteCount))
            {
                MaskRangePreservingNewLines(chars, search, search + quoteCount);
                index = search + quoteCount - 1;
                return true;
            }

            if (interpolationDollarCount > 0 && chars[search] == '{')
            {
                interpolationBraceDepth++;
            }
            else if (interpolationBraceDepth > 0 && chars[search] == '}')
            {
                interpolationBraceDepth--;
            }
            else if (interpolationBraceDepth == 0 && chars[search] is not ('\r' or '\n'))
            {
                chars[search] = ' ';
            }
            search++;
        }

        index = chars.Length - 1;
        return true;
    }

    private static bool TryMaskCSharpInterpolatedString(char[] chars, ref int index)
    {
        var start = index;
        if (chars[start] != '$')
            return false;

        var cursor = start + 1;
        var verbatim = false;
        if (cursor < chars.Length && chars[cursor] == '@')
        {
            verbatim = true;
            cursor++;
        }

        if (cursor >= chars.Length || chars[cursor] != '"')
            return false;

        MaskRangePreservingNewLines(chars, start, cursor + 1);
        var braceDepth = 0;
        for (var i = cursor + 1; i < chars.Length; i++)
        {
            var ch = chars[i];
            var next = i + 1 < chars.Length ? chars[i + 1] : '\0';

            if (braceDepth == 0 && ch == '"' && !(verbatim && next == '"'))
            {
                chars[i] = ' ';
                index = i;
                return true;
            }

            if (verbatim && braceDepth == 0 && ch == '"' && next == '"')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                continue;
            }

            if (!verbatim && braceDepth == 0 && ch == '\\' && next != '\0')
            {
                chars[i] = ' ';
                if (next is not ('\r' or '\n'))
                    chars[i + 1] = ' ';
                i++;
                continue;
            }

            if (ch == '{')
            {
                braceDepth++;
                continue;
            }

            if (braceDepth > 0 && ch == '}')
            {
                braceDepth--;
                continue;
            }

            if (braceDepth == 0 && ch is not ('\r' or '\n'))
                chars[i] = ' ';
        }

        index = chars.Length - 1;
        return true;
    }

    private static bool HasQuoteRun(char[] chars, int start, int quoteCount)
    {
        if (start + quoteCount > chars.Length)
            return false;
        for (var i = 0; i < quoteCount; i++)
        {
            if (chars[start + i] != '"')
                return false;
        }
        return true;
    }

    private static void MaskRangePreservingNewLines(char[] chars, int start, int end)
    {
        for (var i = start; i < end && i < chars.Length; i++)
        {
            if (chars[i] is not ('\r' or '\n'))
                chars[i] = ' ';
        }
    }

    internal static void RegisterConnectionFunctionsWithRetry(
        SqliteConnection connection,
        Action<int>? sleep = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default,
        Action<SqliteConnection>? registerConnectionFunctions = null)
    {
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be at least 1.");

        cancellationToken.ThrowIfCancellationRequested();
        registerConnectionFunctions ??= RegisterConnectionFunctions;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                registerConnectionFunctions(connection);
                return;
            }
            catch (SqliteException ex) when (DbConnectionFactory.IsTransientBusyError(ex) && attempt < maxAttempts)
            {
                DbConnectionFactory.SleepBeforeRetry(50 * attempt, sleep, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Initialize the database schema (tables, indexes, FTS).
    /// データベーススキーマ（テーブル、インデックス、FTS）を初期化する。
    /// </summary>
    // Readiness bitmap stamped into PRAGMA user_version at the end of a successful index.
    // Split so the CLI (graph + issues) and MCP (graph only, no validation pass) can mark
    // different subsets of trust independently.
    // index の成功末尾で user_version に打つビットマップ。CLI と MCP が独立に立てる。
    public const int GraphReadyFlag = 1;
    public const int IssuesReadyFlag = 2;
    // bit 2 (FoldReadyFlag, #86) — name_folded columns (Unicode NFKC + lowerInvariant) fully
    // backfilled on symbols and symbol_references. Set only after a full scan populates every
    // row's folded value so `--exact` queries can use the folded index path for Unicode
    // casing (Ä/ä). Legacy DBs without fold stay on the COLLATE NOCASE fallback until reindex.
    // bit 2 (FoldReadyFlag, #86): name_folded 列の完全バックフィル完了を示す。
    public const int FoldReadyFlag = 4;
    public const int CurrentSchemaVersion = GraphReadyFlag | IssuesReadyFlag | FoldReadyFlag; // 7 — full CLI readiness
    public const int CodeIndexMetaSchemaVersion = 1;
    public const string CodeIndexMetaSchemaVersionMetaKey = "codeindex_meta_schema_version";
    // Query-semantic readiness for hotspot family grouping. Stored in codeindex_meta instead of
    // PRAGMA user_version because this guards a higher-level interpretation contract
    // (`family_key` / `container_qualified_name` are authoritative for the whole DB), not
    // low-level table availability.
    // hotspots family grouping 用 readiness。table の有無ではなく query 意味論の trust を表す。
    public const int HotspotFamilyVersion = 2;
    public const string HotspotFamilyVersionMetaKey = "hotspot_family_version";
    public const string HotspotFamilyMarkerFingerprintMetaKey = "hotspot_family_marker_fingerprint";
    public const string HotspotFamilyIncompleteMarkerFingerprintPrefix = "incomplete:";
    public static string GetHotspotFamilyVersionMetaKey(string lang) => $"hotspot_family_version_{lang}";
    public static string GetHotspotFamilyMarkerFingerprintMetaKey(string lang) => $"hotspot_family_marker_fingerprint_{lang}";
    public static bool IsIncompleteHotspotFamilyMarkerFingerprint(string? fingerprint)
        => !string.IsNullOrWhiteSpace(fingerprint)
           && fingerprint.StartsWith(HotspotFamilyIncompleteMarkerFingerprintPrefix, StringComparison.Ordinal);
    public static string BuildIncompleteHotspotFamilyMarkerFingerprint(string? fingerprint)
        => HotspotFamilyIncompleteMarkerFingerprintPrefix + (string.IsNullOrWhiteSpace(fingerprint) ? "unknown" : fingerprint);
    public const int CSharpSymbolNameContractVersion = 2;
    public const string CSharpSymbolNameContractVersionMetaKey = "csharp_symbol_name_contract_version";
    public const int SqlGraphContractVersion = 1;
    public const string SqlGraphContractVersionMetaKey = "sql_graph_contract_version";
    public const string SymbolsOnlyGraphOmittedMetaKey = "symbols_only_graph_omitted";
    public const string IndexedProjectRootMetaKey = "indexed_project_root";
    // Git HEAD commit captured at the end of the most recent full-scan index run (`--rebuild` or
    // the default incremental full scan). Reading this back lets the CLI detect that a user
    // ran `cdidx index <projectPath>` after switching branches / commits, where the DB still
    // mirrors the previously-indexed worktree even though the on-disk file set has diverged.
    // Partial update modes (`--commits` / `--files`) deliberately do NOT touch this key, so a
    // post-branch-switch partial refresh still surfaces as stale until a real full scan
    // republishes the captured HEAD. The same value is read at `status` time (without
    // `--check`) to surface a worktree branch / HEAD switch via `worktree_head_changed`.
    // Issues #1508 and #1512.
    // 直近の full-scan 成功時点で記録した git HEAD。`cdidx index` 後にブランチが切り替わると
    // DB は旧 worktree のスナップショットのまま残るため、ここを比較して「rebuild を勧める」
    // 警告を出す。partial update (`--commits` / `--files`) は本キーを更新せず、後続の
    // full scan が改めて記録する。同じ値を `status` (no `--check`) でも参照し、
    // `worktree_head_changed` として worktree の HEAD 切替を素早く通知する。Issues #1508 / #1512。
    public const string IndexedHeadCommitMetaKey = "indexed_head_commit";
    public const string IndexedHeadCommitBranchMetaKey = "indexed_head_commit_branch";
    // #1509: full Git HEAD commit and short branch name captured at the end of every
    // successful index run (full scan AND partial update), plus the UTC timestamp of that
    // stamp. Together they let `status` (and any future cross-session staleness check)
    // decide whether the index was built against the commit currently checked out, or
    // whether the working tree has advanced since indexing. This is DIFFERENT from
    // `IndexedHeadCommitMetaKey` above (#1508): that key only fires on full scans so it
    // can drive "rebuild after branch switch" warnings, while these keys fire on every
    // successful index so `commits_ahead_of_indexed_head` reflects the true last-touched
    // HEAD regardless of update mode. Stored as plain strings to keep DbReader's inline
    // codeindex_meta lookup degradation behavior intact on legacy / read-only DBs.
    // #1509: 成功 index (full scan / partial 問わず) の終端で HEAD commit / branch 名 /
    // stamp 時刻を保存する。これにより status などが「DB の HEAD が現在の HEAD と何コミット
    // ズレているか」を検出できる。`IndexedHeadCommitMetaKey` (#1508) とは異なり、こちらは
    // partial update でも更新するため commits_ahead_of_indexed_head が常に正確になる。
    // codeindex_meta が無い legacy DB では reader 側で null フォールバックする。
    public const string IndexedHeadShaMetaKey = "indexed_head_sha";
    public const string IndexedHeadBranchMetaKey = "indexed_head_branch";
    public const string IndexedHeadTimestampMetaKey = "indexed_head_timestamp";
    public const string CommitScopedFreshHeadShaMetaKey = "commit_scoped_fresh_head_sha";
    public const string LastFullScanElapsedMsMetaKey = "last_full_scan_elapsed_ms";
    public const string LastIndexRunModeMetaKey = "last_index_run_mode";
    public const string LastIndexRunStartedAtMetaKey = "last_index_run_started_at";
    public const string LastIndexRunDurationMsMetaKey = "last_index_run_duration_ms";
    public const string LastIndexRunFilesScannedMetaKey = "last_index_run_files_scanned";
    public const string LastIndexRunFilesSkippedMetaKey = "last_index_run_files_skipped";
    public const string LastIndexRunParseErrorsMetaKey = "last_index_run_parse_errors";
    public const string LastIndexRunBytesReadMetaKey = "last_index_run_bytes_read";
    public const string LastIndexRunBytesReadSkippedFileCountMetaKey = "last_index_run_bytes_read_skipped_file_count";
    public const string LastIndexRunBytesReadIncompleteMetaKey = "last_index_run_bytes_read_incomplete";
    public const string LastIndexRunRowsUpsertedMetaKey = "last_index_run_rows_upserted";
    public const string LastIndexRunRowsDeletedMetaKey = "last_index_run_rows_deleted";
    public const string LastIndexRunPeakMemoryMbMetaKey = "last_index_run_peak_memory_mb";
    public const string LastIndexRunDiagnosticsMetaKey = "last_index_run_diagnostics_json";
    public const string LastIndexRunDiagnosticCountMetaKey = "last_index_run_diagnostic_count";
    public const string LastIndexRunDiagnosticsTruncatedMetaKey = "last_index_run_diagnostics_truncated";
    public const int LastIndexRunDiagnosticSampleLimit = 50;
    public const string LastFailedIndexRunStatusMetaKey = "last_failed_index_run_status";
    public const string LastFailedIndexRunModeMetaKey = "last_failed_index_run_mode";
    public const string LastFailedIndexRunStartedAtMetaKey = "last_failed_index_run_started_at";
    public const string LastFailedIndexRunDurationMsMetaKey = "last_failed_index_run_duration_ms";
    public const string LastFailedIndexRunFilesProcessedMetaKey = "last_failed_index_run_files_processed";
    public const string LastFailedIndexRunFilesTotalMetaKey = "last_failed_index_run_files_total";
    public const string LastFailedIndexRunErrorCodeMetaKey = "last_failed_index_run_error_code";
    public const string LastFailedIndexRunReasonMetaKey = "last_failed_index_run_reason";
    public const string LastFailedIndexRunProgressPersistedMetaKey = "last_failed_index_run_progress_persisted";
    public const string LastFailedIndexRunRecoveryHintMetaKey = "last_failed_index_run_recovery_hint";
    // Issue #1585: count of files seen by the most recent successful full-repository scan
    // whose non-empty extension did not map to a known language. This is a scan coverage
    // signal, not an indexed-file count, and is omitted by readers until a current index pass
    // has stamped it.
    // Issue #1585: 直近成功した全体 scan で、非空の拡張子が既知言語に対応しなかった
    // ファイル数。index 済み件数ではなく scan coverage の信号であり、現行 index が stamp
    // するまでは reader 側で省略する。
    public const string UnknownExtensionFileCountMetaKey = "unknown_extension_file_count";
    public const string UnknownExtensionFilePathsMetaKey = "unknown_extension_file_paths_json";
    public const string UnknownExtensionFilesTruncatedMetaKey = "unknown_extension_files_truncated";
    public const string UnknownExtensionFilePathLimitMetaKey = "unknown_extension_file_path_limit";
    public const string UnknownExtensionExtensionCountsMetaKey = "unknown_extension_extension_counts_json";
    public const string UnknownExtensionCategoryCountsMetaKey = "unknown_extension_category_counts_json";
    public const string UnknownExtensionGroupsMetaKey = "unknown_extension_groups_json";
    public const int UnknownExtensionFilePathSampleLimit = 50;
    public const string BatchInProgressMetaKey = "batch_in_progress";
    // Issue #1546: case-sensitivity of the workspace filesystem the most recent successful
    // index ran on, persisted as the string "true" / "false". Resolved via the probe in
    // `PathCasing` (which honors `core.ignorecase` when the project is a git workspace and
    // falls back to a per-volume probe otherwise) so case-sensitive APFS volumes on macOS,
    // case-sensitive NTFS via WSL, and case-sensitive ReFS no longer collapse onto the OS
    // family heuristic. Exposed back through `cdidx status` (`path_case_sensitive`) so
    // operators can diagnose phantom path collapses / missing-file reports.
    // #1546: 直近 index 時のワークスペース FS の大小区別を "true"/"false" で保存する。
    // OS 系列だけに依存していた既存ヒューリスティックでは case-sensitive APFS 等で
    // ファイルが誤って同一視されるため、`PathCasing` の実 FS プローブで判定し、
    // `cdidx status` の `path_case_sensitive` で診断できるようにする。
    public const string WorkspacePathCaseSensitiveMetaKey = "workspace_path_case_sensitive";
    // Authoritative `symbols.is_metadata_target` flag readiness, per language. Stamped at the
    // end of a successful index pass once extractor facts and the writer resolver have
    // classified every class-like row for that language. Readers fall back to the legacy
    // heuristic when the per-language stamp is absent or its version does not match. Issue #3524.
    // 言語別 metadata-target 列の正式 readiness。index 終端で extractor fact と writer resolver が
    // 当該言語の class-like 行を全部分類した後にだけ stamp する。stamp が無い・version 不一致の
    // 言語については reader が legacy ヒューリスティックにフォールバックする。Issue #3524。
    // Version 2 (#435 iter 5) made the writer-side resolver import-aware: unqualified base
    // identifiers now resolve through the deriving file's `using Namespace;` / `using Alias =
    // FQN;` directives (plus `global using` aggregated across the repo) before falling back
    // to the BCL `Attribute`-suffix convention. Iter 4 DBs that only resolved through the
    // deriving class's own scope chain would miss `using A; class FooAttribute : BaseAttr`
    // where `A.BaseAttr : Attribute` is indexed in a sibling file. Bumping the contract
    // forces those DBs to degrade to the legacy `signature LIKE '%: %'` reader path until a
    // reindex republishes `is_metadata_target`.
    // Version 3 (#435 iter 6) normalizes C# verbatim-identifier `@` prefixes on the writer
    // side so `using @Foo.@Bar;`, `using @AliasAttr = @Foo.@BaseAttr;`, and `class Foo :
    // @BaseAttr` resolve identically to their non-verbatim counterparts. Iter-5 DBs stored
    // the raw `@Foo.@Bar` token in the import map and never matched the qualified index,
    // leaving `VerbatimImportAttribute : BaseAttr` as `is_metadata_target=0` and dropping
    // the attribute-consumer edge from `deps` / `impact`. Bumping the contract degrades
    // iter-5 DBs to the legacy reader path until reindexed.
    // Version 4 (#435 iter 7) widens the C# namespace / class / struct / interface / enum
    // declaration regexes to accept verbatim identifiers (`public class @BaseAttr : Attribute`,
    // `namespace @Foo.@Bar`) and canonicalizes the persisted symbol name so the qualified
    // index keys off `BaseAttr` / `Foo.Bar` regardless of source syntax. Iter-6 DBs never
    // indexed verbatim class declarations at all (the extractor regex rejected them), so
    // every derived `class X : @BaseAttr` stayed `is_metadata_target=0` and dropped the
    // attribute edge even with iter-6's base-name stripping in place. Iter 7 also teaches
    // `StripCSharpVerbatimPrefixes` about the `::` boundary so `global::@Foo.@Bar.BaseAttr`
    // canonicalizes all the way to `global::Foo.Bar.BaseAttr` instead of leaving the first
    // `@` after `::` intact. Bumping the contract forces iter-6 DBs to degrade to the
    // legacy reader path until a reindex republishes `is_metadata_target`.
    // バージョン 2 (#435 iter 5)で resolver が import を考慮するようになった。非修飾な基底は
    // deriving ファイルの `using Namespace;` / `using Alias = FQN;`（および全ファイル集約の
    // `global using`）を通して解決してから BCL の `Attribute` サフィックス規約にフォールバック
    // する。iter 4 の DB は `using A; class FooAttribute : BaseAttr` のような一般的な C# パターンで
    // 正しく解決できないため、契約バージョンを上げて reader を legacy ヒューリスティックに縮退
    // させ、再 index で republish されるまで metadata edge を誤って主張させない。
    // バージョン 3 (#435 iter 6) で書き込み側が C# verbatim 識別子の `@` 先頭を正規化するよう
    // になった。`using @Foo.@Bar;` / `using @AliasAttr = @Foo.@BaseAttr;` / `class Foo :
    // @BaseAttr` が非 verbatim 形と同じキーで解決される。iter-5 DB は import map に生の
    // `@Foo.@Bar` を残していたため qualified 索引に当たらず、`VerbatimImportAttribute :
    // BaseAttr` が `is_metadata_target=0` となり attribute consumer 側の edge が落ちていた。
    // 契約バージョンを上げて、再 index 前の iter-5 DB を reader の legacy パスに縮退させる。
    // バージョン 4 (#435 iter 7) で C# の namespace / class / struct / interface / enum 宣言
    // 正規表現が verbatim 識別子（`public class @BaseAttr : Attribute` / `namespace
    // @Foo.@Bar`）を受理するようになり、永続化されるシンボル名も canonical 化される。qualified
    // 索引は `BaseAttr` / `Foo.Bar` としてキー付けされ、ソース表記に依らない。iter-6 DB は
    // verbatim class 宣言自体がインデックスされず（extractor の regex が弾いていた）、
    // `class X : @BaseAttr` のような派生は iter 6 の base 側 `@` 剥がしでも resolve できず
    // `is_metadata_target=0` のまま attribute edge が落ちていた。iter 7 では
    // `StripCSharpVerbatimPrefixes` も `::` 境界を処理するよう拡張し、`global::@Foo.@Bar.BaseAttr`
    // を `global::Foo.Bar.BaseAttr` まで完全に canonical 化する（iter 6 は `::` 直後の `@` を
    // 残していた）。契約バージョンを上げて iter-6 DB を reader の legacy パスに縮退させ、
    // 再 index で republish されるまで metadata edge を黙って誤るのを防ぐ。
    // Version 5 (#435 iter 8) teaches the resolver to expand alias-qualified bases
    // such as `using Alias = A; class FooAttribute : Alias.MetaBase` into
    // `A.MetaBase` before the qualified index lookup. Iter-5 only handled
    // alias-unqualified bases (`class Foo : Alias` where the whole base name is the
    // alias), and the qualified branch fell straight through to the BCL
    // `Attribute`-suffix heuristic — which misses any `MetaBase` real attribute in
    // the alias target namespace unless the derived class happens to be named
    // `...Attribute`. Iter-7 DBs that indexed without this expansion therefore
    // dropped every `[FooAttribute]` edge whose declaration used an alias-qualified
    // base, so the contract is bumped to force a re-index.
    // バージョン 5 (#435 iter 8) で resolver が alias 修飾された基底を展開するようになった。
    // `using Alias = A; class FooAttribute : Alias.MetaBase` の場合、qualified 索引を
    // `A.MetaBase` で引けるようになり、従来は alias 展開が無いまま BCL の `Attribute`
    // サフィックス規約までフォールバックしていたため、alias target 名前空間に居る本物の
    // `MetaBase : Attribute` が同 repo にあっても、派生クラス名が `...Attribute` で終わる
    // 偶然でしか metadata edge を張れなかった。iter-7 DB はこの展開なしで index された
    // ため alias-qualified 基底の edge が黙って落ちていた。契約バージョンを上げて再 index
    // を強制する。
    // Version 6 (#435 iter 9) extends alias-qualified expansion to the `::`
    // separator. C# accepts both `Alias.X` (member access) and `Alias::X`
    // (qualified-alias-member, §7.8) for using aliases that name a namespace,
    // and production code uses the `::` form to disambiguate namespaces from
    // type names. Iter-8 only split on `.` in the expansion helper, so
    // `class FooAttribute : Alias::MetaBase` still fell through to the BCL
    // suffix heuristic and dropped the `[FooAttribute]` edge. Iter-8 DBs that
    // indexed without this expansion must degrade to the legacy reader path
    // until a reindex republishes `is_metadata_target` with `::`-aware
    // resolution.
    // バージョン 6 (#435 iter 9) で alias 修飾展開が `::` 区切りにも対応した。C# では
    // using alias が名前空間を指す場合、`Alias.X`（メンバ アクセス）と `Alias::X`
    // （qualified-alias-member、§7.8）のどちらも許容され、現場コードは名前空間と型
    // 名を衝突させないために `::` を使うことがある。iter-8 の展開 helper は `.` のみで
    // 区切っていたため `class FooAttribute : Alias::MetaBase` は BCL サフィックス規約
    // まで抜け落ち、`[FooAttribute]` の edge が落ちていた。iter-8 DB はこの展開なしで
    // index されたため、再 index で `::` 対応の resolver が `is_metadata_target` を
    // republish するまで reader を legacy 経路へ縮退させる。
    // Version 7 (#3524) persists metadata-target provenance in
    // `symbols.metadata_target_source` so readers and diagnostics can tell direct extractor
    // facts from writer-resolved transitive targets. Iter-6 DBs only stored the flattened
    // `is_metadata_target` bit, so they must degrade until reindexed with source-aware
    // storage.
    // バージョン 7 (#3524) で `symbols.metadata_target_source` に provenance を保存する。
    // extractor が直接検出した fact と writer が推移的に解決した target を reader / diagnostics
    // が区別できるようにするため、平坦な `is_metadata_target` だけを持つ iter-6 DB は
    // source-aware storage で再 index されるまで縮退させる。
    public const int MetadataTargetVersion = 7;
    public static string GetMetadataTargetVersionMetaKey(string lang) => $"metadata_target_version_{lang}";
    // Audit trail: cdidx version string (e.g. "1.22.0") that produced the most recent
    // successful end-of-index pass on this DB. Readers use it to surface "DB written by
    // a newer cdidx" warnings when any persisted contract version exceeds this binary's
    // compiled max so silent rollback / mixed-version-team degradation becomes visible.
    // Issue #1515.
    // 監査用: 成功 index の末尾に書き込んだ cdidx の version 文字列。reader はここと
    // 各種 contract version の比較で「より新しい cdidx が書いた DB」を検知し、
    // 黙って縮退するのではなく status で警告するために利用する。Issue #1515。
    public const string CdidxWriterVersionMetaKey = "cdidx_writer_version";

    public int GetUserVersion()
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = "PRAGMA user_version";
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : (result is int i ? i : 0);
    }

    // Reset readiness bits. Called at the START of every index run so an interrupted run
    // on an already-stamped DB demotes the trust signal to degraded until the end-of-run
    // stamp is written on fully successful completion.
    // index 開始時にビットをクリア。途中で落ちた場合は縮退状態のまま残す。
    public void ClearReadyFlags()
    {
        Execute("PRAGMA user_version = 0");
    }

    /// <summary>
    /// Read a string value from `codeindex_meta`. Returns null when absent or the table
    /// hasn't been created (legacy DBs, read-only sandboxes where migration was skipped).
    /// codeindex_meta からの読み取り。テーブル未作成や未登録キーは null を返す。
    /// </summary>
    public string? GetMetaString(string key)
    {
        if (!TableExists("codeindex_meta")) return null;
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
        SqliteCommandPolicy.Add(cmd, "@key", key);
        var raw = cmd.ExecuteScalar();
        return raw is string s ? s : null;
    }

    public bool TryValidateIsCodeIndexDb(out string? reason)
    {
        var requiredTables = new[] { "files", "symbols" };
        foreach (var table in requiredTables)
        {
            if (!TableExists(table))
            {
                reason = $"missing required table `{table}`";
                return false;
            }
        }

        reason = null;
        return true;
    }

    private bool TableExists(string name)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        SqliteCommandPolicy.Add(cmd, "@name", name);
        return cmd.ExecuteScalar() != null;
    }

    public void InitializeSchema()
    {
        var legacyAlterTable = ExecuteScalar("PRAGMA legacy_alter_table");
        var foreignKeys = ReadPragmaLong("foreign_keys");
        Execute("PRAGMA foreign_keys=OFF");
        Execute("PRAGMA legacy_alter_table=ON");
        try
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            _activeMigrationTransaction = transaction;
            try
            {
                // Files table / ファイルテーブル
                Execute(@"
            CREATE TABLE IF NOT EXISTS files (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                path        TEXT    NOT NULL UNIQUE,
                lang        TEXT,
                size        INTEGER,
                lines       INTEGER,
                checksum    TEXT,
                modified    DATETIME,
                generated   INTEGER NOT NULL DEFAULT 0,
                indexed_at  DATETIME DEFAULT CURRENT_TIMESTAMP
            )");

                // Chunks table / チャンクテーブル
                Execute(@"
            CREATE TABLE IF NOT EXISTS chunks (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                chunk_index INTEGER NOT NULL,
                start_line  INTEGER,
                end_line    INTEGER,
                content     TEXT,
                UNIQUE(file_id, chunk_index)
            )");

                // Shared reference-line context table / 参照行コンテキスト共有テーブル
                Execute(@"
            CREATE TABLE IF NOT EXISTS reference_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                line        INTEGER NOT NULL,
                context     TEXT NOT NULL,
                UNIQUE(file_id, line, context)
            )");

                var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
                var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);

                // Symbols table / シンボルテーブル
                Execute(@"
            CREATE TABLE IF NOT EXISTS symbols (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT CHECK (kind IN (" + symbolKindCheck + @")),
                sub_kind        TEXT,
                name            TEXT,
                line            INTEGER,
                start_line      INTEGER,
                start_column    INTEGER,
                end_line        INTEGER,
                body_start_line INTEGER,
                body_end_line   INTEGER,
                signature       TEXT,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN (" + symbolKindCheck + @")),
                container_name  TEXT,
                container_qualified_name TEXT,
                family_key      TEXT,
                visibility      TEXT,
                return_type     TEXT,
                is_metadata_target INTEGER,
                metadata_target_source TEXT
            )");

                // Indexed references table / 参照インデックステーブル
                Execute(@"
            CREATE TABLE IF NOT EXISTS symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN (" + referenceKindCheck + @")),
                line            INTEGER,
                column_number   INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN (" + symbolKindCheck + @")),
                container_name  TEXT
            )");

                // File validation issues table / ファイル検証問題テーブル
                Execute(@"
            CREATE TABLE IF NOT EXISTS file_issues (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT NOT NULL,
                line            INTEGER NOT NULL DEFAULT 0,
                message         TEXT NOT NULL,
                origin          TEXT,
                severity        TEXT
            )");

                // Key-value metadata: fold algorithm version, future per-subsystem schema markers
                // that don't fit in PRAGMA user_version's 3-bit readiness bitmap. See
                // NameFold.Version and DbReader fold-ready gate.
                // メタデータ用 key-value: fold のアルゴリズム版数など、user_version bitmap に収まらない情報。
                Execute(@"
            CREATE TABLE IF NOT EXISTS codeindex_meta (
                key    TEXT PRIMARY KEY NOT NULL,
                value  TEXT
            )");
                NormalizeCodeIndexMetaKeys();

                // Schema migrations for existing DBs / 既存DB向けスキーマ移行
                EnsureColumn("files", "checksum", "TEXT");
                EnsureColumn("files", "modified", "DATETIME");
                EnsureColumn("files", "generated", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn("files", "indexed_at", "DATETIME");
                EnsureColumn("symbols", "start_line", "INTEGER");
                EnsureColumn("symbols", "sub_kind", "TEXT");
                EnsureColumn("symbols", "start_column", "INTEGER");
                EnsureColumn("symbols", "end_line", "INTEGER");
                EnsureColumn("symbols", "body_start_line", "INTEGER");
                EnsureColumn("symbols", "body_end_line", "INTEGER");
                EnsureColumn("symbols", "signature", "TEXT");
                EnsureColumn("symbols", "container_kind", "TEXT");
                EnsureColumn("symbols", "container_name", "TEXT");
                EnsureColumn("symbols", "container_qualified_name", "TEXT");
                EnsureColumn("symbols", "family_key", "TEXT");
                EnsureColumn("symbols", "visibility", "TEXT");
                EnsureColumn("symbols", "return_type", "TEXT");
                EnsureColumn("file_issues", "origin", "TEXT");
                EnsureColumn("file_issues", "severity", "TEXT");
                EnsureColumn("symbols", "is_metadata_target", "INTEGER");
                EnsureColumn("symbols", "metadata_target_source", "TEXT");
                var rebuildsSymbolReferences = !ColumnIsNotNull("symbol_references", "file_id");
                EnsureColumn(
                    "symbol_references",
                    "reference_line_id",
                    rebuildsSymbolReferences ? "INTEGER" : "INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL");
                // #86: Unicode-aware folded name columns for `--exact` name matching across all
                // `--exact` command variants. Populated by the writer via NameFold.Fold; NULL on
                // legacy rows until a full reindex, in which case the reader falls back to the
                // COLLATE NOCASE path (correct for ASCII, misses non-ASCII casing — #86 fix).
                // #86: --exact 用の Unicode 折り畳み列。レガシー行は NULL のまま、再 index で埋まる。
                EnsureColumn("symbols", "name_folded", "TEXT");
                EnsureColumn("symbol_references", "symbol_name_folded", "TEXT");
                EnsureColumn("symbol_references", "container_name_folded", "TEXT");
                EnsureColumn("symbol_references", "is_self_reference", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn("symbol_references", "is_mutual_recursion", "INTEGER NOT NULL DEFAULT 0");
                EnforceRequiredFileIdConstraints();
                EnforceReferenceLineSetNullConstraint();
                EnsureReferenceLinesContextKey();
                EnsureKindCheckConstraintsCurrent();

                // Indexes / インデックス
                Execute("CREATE INDEX IF NOT EXISTS idx_files_lang     ON files(lang)");
                Execute("CREATE INDEX IF NOT EXISTS idx_files_modified ON files(modified)");
                Execute("CREATE INDEX IF NOT EXISTS idx_files_generated ON files(generated)");
                // idx_files_path is not needed: the UNIQUE constraint on path already creates an implicit index
                // idx_files_path は不要: path の UNIQUE 制約が暗黙的にインデックスを作成済み
                Execute("CREATE INDEX IF NOT EXISTS idx_chunks_file    ON chunks(file_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name   ON symbols(name)");
                // Case-insensitive exact-match index for `symbols --exact` (and MCP `symbols` exact=true).
                // Without this, `name = @q COLLATE NOCASE` falls back to a full symbols scan per query name,
                // which on multi-name exact lookups becomes O(names × symbols).
                // `symbols --exact` 用の大文字小文字無視 index。無いと multi-name exact でフルスキャンが N 回走る。
                Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_nocase ON symbols(name COLLATE NOCASE)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbols_file   ON symbols(file_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbols_start  ON symbols(start_line)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name      ON symbol_references(symbol_name)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_file      ON symbol_references(file_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container ON symbol_references(container_name)");
                // Compound indexes for common query patterns / よくあるクエリパターン用の複合インデックス
                Execute("CREATE INDEX IF NOT EXISTS idx_symbols_file_kind      ON symbols(file_id, kind)");
                Execute("CREATE INDEX IF NOT EXISTS idx_files_lang_modified     ON files(lang, modified)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_kind ON symbol_references(container_name, reference_kind)");
                // Indexes for new query patterns: --kind filter, visibility ranking, hotspot/unused analysis
                // 新しいクエリパターン用: --kind フィルタ、可視性ランキング、ホットスポット/未使用分析
                Execute("CREATE INDEX IF NOT EXISTS idx_symbols_kind            ON symbols(kind)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbols_visibility      ON symbols(visibility)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_kind   ON symbol_references(symbol_name, reference_kind)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_file   ON symbol_references(symbol_name, file_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_mutual_folded ON symbol_references(container_name_folded, symbol_name_folded, reference_kind, is_self_reference)");
                Execute("CREATE INDEX IF NOT EXISTS idx_reference_lines_file_line ON reference_lines(file_id, line)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_reference_line ON symbol_references(reference_line_id)");
                // Case-insensitive exact-match indexes for `references --exact` / `callers --exact` / `callees --exact` (#83).
                // Mirror idx_symbols_name_nocase so `= @q COLLATE NOCASE` stays O(log n) per name across graph commands.
                // `references / callers / callees --exact` 用の NOCASE index。idx_symbols_name_nocase と対になる。
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase      ON symbol_references(symbol_name COLLATE NOCASE)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase ON symbol_references(container_name COLLATE NOCASE)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)");
                // #86: Indexes on the Unicode-folded columns. Used when FoldReadyFlag is set on the
                // DB (= the write path filled every folded column). Legacy / partial DBs keep using
                // the NOCASE indexes above. Both sets coexist so mixed-state DBs cannot regress.
                // #86: 折り畳み列のインデックス。FoldReadyFlag が立っている DB でだけ使う。
                Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_folded                ON symbols(name_folded)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded     ON symbol_references(symbol_name_folded)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded  ON symbol_references(container_name_folded)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_kind ON symbol_references(symbol_name_folded, reference_kind)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_file ON symbol_references(symbol_name_folded, file_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded_kind ON symbol_references(container_name_folded, reference_kind)");

                // Full-text search / 全文検索
                Execute(@"
            CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5(
                content,
                content='chunks',
                content_rowid='id'
            )");
                if (_rebuildFtsAfterSchemaMigration)
                {
                    Execute("INSERT INTO fts_chunks(fts_chunks) VALUES('rebuild')");
                    _rebuildFtsAfterSchemaMigration = false;
                }

                // FTS5 content-synced triggers — keep fts_chunks in sync with chunks table.
                // Without these, CASCADE DELETEs on chunks leave orphan entries in fts_chunks.
                // FTS5 content-synced トリガー — fts_chunksをchunksテーブルと同期する。
                // これがないとchunksのCASCADE DELETEでfts_chunksに孤立エントリが残る。
                Execute(@"
            CREATE TRIGGER IF NOT EXISTS fts_chunks_ai AFTER INSERT ON chunks BEGIN
                INSERT INTO fts_chunks(rowid, content) VALUES (new.id, new.content);
            END");
                Execute(@"
            CREATE TRIGGER IF NOT EXISTS fts_chunks_ad AFTER DELETE ON chunks BEGIN
                INSERT INTO fts_chunks(fts_chunks, rowid, content) VALUES('delete', old.id, old.content);
            END");
                Execute(@"
            CREATE TRIGGER IF NOT EXISTS fts_chunks_au AFTER UPDATE ON chunks BEGIN
                INSERT INTO fts_chunks(fts_chunks, rowid, content) VALUES('delete', old.id, old.content);
                INSERT INTO fts_chunks(rowid, content) VALUES (new.id, new.content);
            END");
                transaction.Commit();
            }
            finally
            {
                _activeMigrationTransaction = null;
            }
        }
        finally
        {
            Execute($"PRAGMA foreign_keys={foreignKeys}");
            Execute($"PRAGMA legacy_alter_table={legacyAlterTable}");
            _schemaCache?.Refresh();
        }
    }

    private void EnforceRequiredFileIdConstraints()
    {
        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        Execute("PRAGMA foreign_keys=OFF");
        var legacyAlterTable = ExecuteScalar("PRAGMA legacy_alter_table");
        Execute("PRAGMA legacy_alter_table=ON");
        try
        {
            RebuildTableWithRequiredFileId(
                "chunks",
                """
                CREATE TABLE chunks (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    chunk_index INTEGER NOT NULL,
                    start_line  INTEGER,
                    end_line    INTEGER,
                    content     TEXT,
                    UNIQUE(file_id, chunk_index)
                )
                """,
                "id, file_id, chunk_index, start_line, end_line, content");
            RebuildTableWithRequiredFileId(
                "symbols",
                $"""
                CREATE TABLE symbols (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    kind            TEXT CHECK (kind IN ({symbolKindCheck})),
                    sub_kind        TEXT,
                    name            TEXT,
                    line            INTEGER,
                    start_line      INTEGER,
                    start_column    INTEGER,
                    end_line        INTEGER,
                    body_start_line INTEGER,
                    body_end_line   INTEGER,
                    signature       TEXT,
                    container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                    container_name  TEXT,
                    container_qualified_name TEXT,
                    family_key      TEXT,
                    visibility      TEXT,
                    return_type     TEXT,
                    is_metadata_target INTEGER,
                    metadata_target_source TEXT,
                    name_folded     TEXT
                )
                """,
                "id, file_id, kind, sub_kind, name, line, start_line, start_column, end_line, body_start_line, body_end_line, signature, container_kind, container_name, container_qualified_name, family_key, visibility, return_type, is_metadata_target, metadata_target_source, name_folded");
            RebuildReferenceLineTablesWithRequiredFileId();
            RebuildTableWithRequiredFileId(
                "file_issues",
                """
                CREATE TABLE file_issues (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    kind            TEXT NOT NULL,
                    line            INTEGER NOT NULL DEFAULT 0,
                    message         TEXT NOT NULL,
                    origin          TEXT,
                    severity        TEXT
                )
                """,
                "id, file_id, kind, line, message, origin, severity");
        }
        finally
        {
            Execute($"PRAGMA legacy_alter_table={legacyAlterTable}");
            Execute("PRAGMA foreign_keys=ON");
        }
    }

    private void RebuildReferenceLineTablesWithRequiredFileId()
    {
        if (ColumnIsNotNull("reference_lines", "file_id") &&
            ColumnIsNotNull("symbol_references", "file_id"))
        {
            return;
        }

        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);
        const string referenceLinesCreateSql =
            """
            CREATE TABLE reference_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                line        INTEGER NOT NULL,
                context     TEXT NOT NULL,
                UNIQUE(file_id, line, context)
            )
            """;
        const string referenceLinesColumns = "id, file_id, line, context";
        var symbolReferencesCreateSql =
            $"""
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ({referenceKindCheck})),
                line            INTEGER,
                column_number   INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0
            )
            """;
        const string symbolReferencesColumns = "id, file_id, symbol_name, reference_kind, line, column_number, context, reference_line_id, container_kind, container_name, symbol_name_folded, container_name_folded, is_self_reference, is_mutual_recursion";

        const string oldReferenceLines = "_reference_lines_nullable_file_id";
        const string oldSymbolReferences = "_symbol_references_nullable_file_id";
        var quotedOldReferenceLines = SqliteIdentifier.Quote(oldReferenceLines);
        var quotedOldSymbolReferences = SqliteIdentifier.Quote(oldSymbolReferences);
        Execute($"DROP TABLE IF EXISTS {quotedOldSymbolReferences}");
        Execute($"DROP TABLE IF EXISTS {quotedOldReferenceLines}");
        Execute("DELETE FROM symbol_references WHERE file_id IS NULL");
        Execute("DELETE FROM reference_lines WHERE file_id IS NULL");
        Execute($"ALTER TABLE symbol_references RENAME TO {quotedOldSymbolReferences}");
        Execute($"ALTER TABLE reference_lines RENAME TO {quotedOldReferenceLines}");
        Execute(referenceLinesCreateSql);
        Execute($"INSERT INTO reference_lines ({referenceLinesColumns}) SELECT {referenceLinesColumns} FROM {quotedOldReferenceLines}");
        Execute(symbolReferencesCreateSql);
        Execute($"INSERT INTO symbol_references ({symbolReferencesColumns}) SELECT {symbolReferencesColumns} FROM {quotedOldSymbolReferences}");
        Execute($"DROP TABLE {quotedOldSymbolReferences}");
        Execute($"DROP TABLE {quotedOldReferenceLines}");
    }

    private void EnforceReferenceLineSetNullConstraint()
    {
        if (SymbolReferencesReferenceLineDeletesSetNull())
            return;

        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);
        var symbolReferencesCreateSql =
            $"""
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ({referenceKindCheck})),
                line            INTEGER,
                column_number   INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0
            )
            """;
        const string symbolReferencesColumns = "id, file_id, symbol_name, reference_kind, line, column_number, context, reference_line_id, container_kind, container_name, symbol_name_folded, container_name_folded, is_self_reference, is_mutual_recursion";
        const string oldSymbolReferences = "_symbol_references_reference_line_delete";
        var quotedOldSymbolReferences = SqliteIdentifier.Quote(oldSymbolReferences);

        Execute($"DROP TABLE IF EXISTS {quotedOldSymbolReferences}");
        Execute(@"
            UPDATE symbol_references
            SET reference_line_id = NULL
            WHERE reference_line_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM reference_lines
                  WHERE reference_lines.id = symbol_references.reference_line_id
              )");
        Execute($"ALTER TABLE symbol_references RENAME TO {quotedOldSymbolReferences}");
        Execute(symbolReferencesCreateSql);
        Execute($"INSERT INTO symbol_references ({symbolReferencesColumns}) SELECT {symbolReferencesColumns} FROM {quotedOldSymbolReferences}");
        Execute($"DROP TABLE {quotedOldSymbolReferences}");
    }

    private bool SymbolReferencesReferenceLineDeletesSetNull()
    {
        using var cmd = _connection.CreateCommand();
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "PRAGMA foreign_key_list('symbol_references')";

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var table = reader.GetString(2);
            var from = reader.GetString(3);
            var onDelete = reader.GetString(6);
            if (string.Equals(table, "reference_lines", StringComparison.OrdinalIgnoreCase)
                && string.Equals(from, "reference_line_id", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(onDelete, "SET NULL", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private void EnsureReferenceLinesContextKey()
    {
        if (ReferenceLinesHasContextUniqueKey())
            return;

        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);
        const string referenceLinesCreateSql =
            """
            CREATE TABLE reference_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                line        INTEGER NOT NULL,
                context     TEXT NOT NULL,
                UNIQUE(file_id, line, context)
            )
            """;
        const string referenceLinesColumns = "id, file_id, line, context";
        var symbolReferencesCreateSql =
            $"""
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ({referenceKindCheck})),
                line            INTEGER,
                column_number   INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0
            )
            """;
        const string symbolReferencesColumns = "id, file_id, symbol_name, reference_kind, line, column_number, context, reference_line_id, container_kind, container_name, symbol_name_folded, container_name_folded, is_self_reference, is_mutual_recursion";

        const string oldReferenceLines = "_reference_lines_file_line_key";
        const string oldSymbolReferences = "_symbol_references_file_line_key";
        var quotedOldReferenceLines = SqliteIdentifier.Quote(oldReferenceLines);
        var quotedOldSymbolReferences = SqliteIdentifier.Quote(oldSymbolReferences);
        RunWithForeignKeysDisabledForMigration("EnsureReferenceLinesContextKey", () =>
        {
            Execute($"DROP TABLE IF EXISTS {quotedOldSymbolReferences}");
            Execute($"DROP TABLE IF EXISTS {quotedOldReferenceLines}");
            Execute($"ALTER TABLE symbol_references RENAME TO {quotedOldSymbolReferences}");
            Execute($"ALTER TABLE reference_lines RENAME TO {quotedOldReferenceLines}");
            Execute(referenceLinesCreateSql);
            Execute($"INSERT INTO reference_lines ({referenceLinesColumns}) SELECT {referenceLinesColumns} FROM {quotedOldReferenceLines}");
            Execute(symbolReferencesCreateSql);
            Execute($"INSERT INTO symbol_references ({symbolReferencesColumns}) SELECT {symbolReferencesColumns} FROM {quotedOldSymbolReferences}");
            Execute($"DROP TABLE {quotedOldSymbolReferences}");
            Execute($"DROP TABLE {quotedOldReferenceLines}");
            InvokeForeignKeyValidationBeforeCheckForTesting("reference_lines_context_key");
        });

        ValidateForeignKeysAfterMigration("reference_lines_context_key");
        _schemaCache?.Refresh();
    }

    private bool ReferenceLinesHasContextUniqueKey()
    {
        using var listCmd = SqliteConnectionPolicy.CreateCommand(_connection);
        listCmd.CommandText = "PRAGMA index_list('reference_lines')";
        using var indexReader = listCmd.ExecuteReader();
        var indexNames = new List<string>();
        while (indexReader.Read())
        {
            var isUnique = indexReader.GetInt32(2) == 1;
            if (isUnique)
                indexNames.Add(indexReader.GetString(1));
        }

        foreach (var indexName in indexNames)
        {
            using var infoCmd = SqliteConnectionPolicy.CreateCommand(_connection);
            infoCmd.CommandText = $"PRAGMA index_info('{indexName.Replace("'", "''")}')";
            using var infoReader = infoCmd.ExecuteReader();
            var columns = new List<string>();
            while (infoReader.Read())
                columns.Add(infoReader.GetString(2));

            if (columns.SequenceEqual(["file_id", "line", "context"], StringComparer.Ordinal))
                return true;
        }

        return false;
    }

    private void EnsureKindCheckConstraintsCurrent()
    {
        var symbolKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds);
        var referenceKindCheck = SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.ReferenceKinds);
        var symbolsCreateSql =
            $"""
            CREATE TABLE symbols (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT CHECK (kind IN ({symbolKindCheck})),
                sub_kind        TEXT,
                name            TEXT,
                line            INTEGER,
                start_line      INTEGER,
                start_column    INTEGER,
                end_line        INTEGER,
                body_start_line INTEGER,
                body_end_line   INTEGER,
                signature       TEXT,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                container_qualified_name TEXT,
                family_key      TEXT,
                visibility      TEXT,
                return_type     TEXT,
                is_metadata_target INTEGER,
                metadata_target_source TEXT,
                name_folded     TEXT
            )
            """;
        const string symbolsColumns = "id, file_id, kind, sub_kind, name, line, start_line, start_column, end_line, body_start_line, body_end_line, signature, container_kind, container_name, container_qualified_name, family_key, visibility, return_type, is_metadata_target, metadata_target_source, name_folded";
        var symbolReferencesCreateSql =
            $"""
            CREATE TABLE symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT CHECK (reference_kind IN ({referenceKindCheck})),
                line            INTEGER,
                column_number   INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT CHECK (container_kind IS NULL OR container_kind IN ({symbolKindCheck})),
                container_name  TEXT,
                symbol_name_folded TEXT,
                container_name_folded TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0
            )
            """;
        const string symbolReferencesColumns = "id, file_id, symbol_name, reference_kind, line, column_number, context, reference_line_id, container_kind, container_name, symbol_name_folded, container_name_folded, is_self_reference, is_mutual_recursion";

        var rebuilt = false;
        RunWithForeignKeysDisabledForMigration("EnsureKindCheckConstraintsCurrent", () =>
        {
            if (!TableCheckContainsAll("symbols", SymbolKindCatalog.SymbolKinds))
            {
                RebuildTableWithCurrentKindChecks("symbols", "_symbols_kind_check", symbolsCreateSql, symbolsColumns);
                rebuilt = true;
            }

            if (!TableCheckContainsAll("symbol_references", SymbolKindCatalog.SymbolKinds.Concat(SymbolKindCatalog.ReferenceKinds)))
            {
                RebuildTableWithCurrentKindChecks("symbol_references", "_symbol_references_kind_check", symbolReferencesCreateSql, symbolReferencesColumns);
                rebuilt = true;
            }

            if (rebuilt)
                InvokeForeignKeyValidationBeforeCheckForTesting("kind_check_constraints");
        });

        if (rebuilt)
            ValidateForeignKeysAfterMigration("kind_check_constraints");
    }

    private void RunWithForeignKeysDisabledForMigration(string operation, Action action)
    {
        var foreignKeys = ReadPragmaLong("foreign_keys");
        Execute("PRAGMA foreign_keys=OFF");
        ExceptionDispatchInfo? operationFailure = null;
        try
        {
            ForeignKeysDisabledForTesting?.Invoke(operation);
            action();
        }
        catch (Exception ex)
        {
            operationFailure = ExceptionDispatchInfo.Capture(ex);
        }

        try
        {
            ForeignKeysRestoringForTesting?.Invoke(operation, foreignKeys);
            Execute($"PRAGMA foreign_keys={foreignKeys}");
        }
        catch (Exception ex)
        {
            throw new CodeIndexException(
                code: CommandErrorCodes.DbError,
                category: CodeIndexExceptionCategory.Database,
                message: $"Failed to restore PRAGMA foreign_keys after {operation}.",
                path: _connection.DataSource,
                hint: "Close other database connections, restore write access if needed, and rerun the command before trusting further migration work.",
                innerException: ex);
        }

        operationFailure?.Throw();
    }

    private void InvokeForeignKeyValidationBeforeCheckForTesting(string phase)
    {
        var boundedPhase = DiagnosticRedactor.BoundDiagnosticText(phase, MigrationDiagnosticTextLimit);
        ForeignKeyValidationBeforeCheckForTesting?.Invoke(_connection, boundedPhase);
    }

    private void ValidateForeignKeysAfterMigration(string phase)
    {
        var boundedPhase = DiagnosticRedactor.BoundDiagnosticText(phase, MigrationDiagnosticTextLimit);
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "PRAGMA foreign_key_check";

        var violations = new List<string>();
        var violationCount = 0;
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            violationCount++;
            if (violations.Count < MigrationForeignKeyViolationSampleLimit)
                violations.Add(FormatForeignKeyViolation(reader));
        }

        if (violationCount == 0)
            return;

        var sample = string.Join("; ", violations);
        var truncated = violationCount > violations.Count
            ? $" (showing {violations.Count.ToString(CultureInfo.InvariantCulture)})"
            : string.Empty;
        throw new CodeIndexException(
            code: CommandErrorCodes.DbIntegrityFailed,
            category: CodeIndexExceptionCategory.Database,
            message: $"Foreign key validation failed after schema migration phase '{boundedPhase}' with {violationCount.ToString(CultureInfo.InvariantCulture)} violation(s){truncated}: {sample}.",
            hint: "Run `cdidx db --integrity-check --db <db>` and rebuild the index on writable storage if violations persist.");
    }

    private static string FormatForeignKeyViolation(SqliteDataReader reader)
    {
        var table = FormatForeignKeyCheckValue(reader.IsDBNull(0) ? "<unknown>" : reader.GetString(0));
        var rowId = reader.IsDBNull(1)
            ? "<null>"
            : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        var parent = FormatForeignKeyCheckValue(reader.IsDBNull(2) ? "<unknown>" : reader.GetString(2));
        var fkId = reader.IsDBNull(3)
            ? "<null>"
            : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return $"table={table}, rowid={rowId}, parent={parent}, fkid={fkId}";
    }

    private static string FormatForeignKeyCheckValue(string value)
        => DiagnosticRedactor.BoundDiagnosticText(
            DiagnosticRedactor.RedactSensitiveText(value, redactPaths: true),
            MigrationDiagnosticTextLimit);

    private bool TableCheckContainsAll(string tableName, IEnumerable<string> allowedValues)
    {
        var createSql = GetTableCreateSql(tableName);
        if (createSql == null)
            return true;

        if (!createSql.Contains("CHECK", StringComparison.OrdinalIgnoreCase))
            return true;

        return allowedValues.All(value => createSql.Contains($"'{value.Replace("'", "''")}'", StringComparison.Ordinal));
    }

    private string? GetTableCreateSql(string tableName)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table";
        SqliteCommandPolicy.AddText(cmd, "@table", tableName);
        return cmd.ExecuteScalar() as string;
    }

    private string BuildRebuildSelectProjection(string sourceTableName, string columns)
    {
        var existingColumns = LoadColumnNames(sourceTableName);
        var projectedColumns = columns
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(column => existingColumns.Contains(column) ? column : $"NULL AS {column}");
        return string.Join(", ", projectedColumns);
    }

    private HashSet<string> LoadColumnNames(string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(tableName);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private void RebuildTableWithCurrentKindChecks(string tableName, string oldTableName, string createSql, string columns)
    {
        var quotedTableName = SqliteIdentifier.Quote(tableName);
        var quotedOldTableName = SqliteIdentifier.Quote(oldTableName);
        Execute($"DROP TABLE IF EXISTS {quotedOldTableName}");
        Execute($"ALTER TABLE {quotedTableName} RENAME TO {quotedOldTableName}");
        Execute(createSql);
        var sourceColumns = BuildRebuildSelectProjection(oldTableName, columns);
        Execute($"INSERT INTO {quotedTableName} ({columns}) SELECT {sourceColumns} FROM {quotedOldTableName}");
        Execute($"DROP TABLE {quotedOldTableName}");
    }

    private void RebuildTableWithRequiredFileId(string tableName, string createSql, string columns)
    {
        if (ColumnIsNotNull(tableName, "file_id"))
            return;

        var oldTableName = $"_{tableName}_nullable_file_id";
        var quotedTableName = SqliteIdentifier.Quote(tableName);
        var quotedOldTableName = SqliteIdentifier.Quote(oldTableName);
        Execute($"DROP TABLE IF EXISTS {quotedOldTableName}");
        Execute($"DROP TRIGGER IF EXISTS fts_chunks_ai");
        Execute($"DROP TRIGGER IF EXISTS fts_chunks_ad");
        Execute($"DROP TRIGGER IF EXISTS fts_chunks_au");
        if (string.Equals(tableName, "chunks", StringComparison.Ordinal))
        {
            Execute("DROP TABLE IF EXISTS fts_chunks");
            _rebuildFtsAfterSchemaMigration = true;
        }
        Execute($"DELETE FROM {quotedTableName} WHERE file_id IS NULL");
        Execute($"ALTER TABLE {quotedTableName} RENAME TO {quotedOldTableName}");
        Execute(createSql);
        var sourceColumns = BuildRebuildSelectProjection(oldTableName, columns);
        Execute($"INSERT INTO {quotedTableName} ({columns}) SELECT {sourceColumns} FROM {quotedOldTableName}");
        if (!string.Equals(tableName, "reference_lines", StringComparison.Ordinal))
            Execute($"DROP TABLE {quotedOldTableName}");
    }

    private bool ColumnIsNotNull(string tableName, string columnName)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(tableName);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(3) != 0;
        }
        return false;
    }

    /// <summary>
    /// Delete all data for a full rebuild.
    /// 全データを削除して完全再構築する。
    /// </summary>
    public void DropAll()
    {
        Execute("DROP TRIGGER IF EXISTS fts_chunks_ai");
        Execute("DROP TRIGGER IF EXISTS fts_chunks_ad");
        Execute("DROP TRIGGER IF EXISTS fts_chunks_au");
        Execute("DROP TABLE IF EXISTS fts_chunks");
        Execute("DROP TABLE IF EXISTS file_issues");
        Execute("DROP TABLE IF EXISTS symbol_references");
        Execute("DROP TABLE IF EXISTS reference_lines");
        Execute("DROP TABLE IF EXISTS symbols");
        Execute("DROP TABLE IF EXISTS chunks");
        Execute("DROP TABLE IF EXISTS files");
        _schemaCache?.Refresh();
    }

    private void Execute(string sql)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        MarkWriteWork(walCheckpointable: false);
    }

    private void EnsureForeignKeysEnabled()
    {
        Execute("PRAGMA foreign_keys=ON");
        var fkResult = ExecuteScalar("PRAGMA foreign_keys");
        if (fkResult != "1")
            CommandErrorWriter.WriteStderr("Warning: foreign_keys pragma not enabled");
    }

    /// <summary>
    /// Latest opportunistic-migration failure captured by <see cref="TryMigrateForRead"/>.
    /// Null when the most recent migration attempt completed every step (or was skipped on a
    /// read-only connection). Callers can surface this to explain a later "no such column"
    /// error coming out of a read path.
    /// 直前の <see cref="TryMigrateForRead"/> 実行で発生した部分マイグレーション失敗の情報。
    /// 全ステップ完了時、または読み取り専用接続でスキップされた場合は null。
    /// </summary>
    public DbMigrationFailure? LastMigrationFailure { get; private set; }

    /// <summary>
    /// Attempt opportunistic schema migration for read-only query paths.
    /// Failures are captured on <see cref="LastMigrationFailure"/> and a single
    /// actionable warning is written to <see cref="Console.Error"/> so a later
    /// "no such column" error can be tied back to the failing migration step.
    /// 読み取り専用クエリパス向けの機会的スキーマ移行を試みる。
    /// 失敗時は <see cref="LastMigrationFailure"/> に記録し、stderr に 1 行の警告を出す。
    /// </summary>
    public void TryMigrateForRead()
    {
        // Skip migration entirely on read-only connections. Even CREATE TABLE IF NOT EXISTS
        // fails with SQLITE_CANTOPEN on sandboxes that cannot create -journal side files —
        // previously only SQLITE_READONLY was caught, so the normal --db /path flow threw
        // on restricted mounts even after the constructor had already degraded to read-only.
        // read-only 接続ではマイグレーション DDL 自体を走らせない。CANTOPEN が漏れて落ちるため。
        if (_isReadOnly) return;

        LastMigrationFailure = null;
        if (ReadMigrationSchemaIsCurrent())
            return;

        try
        {
            EnsureForeignKeysEnabled();
            SqliteTransaction? transaction;
            try
            {
                transaction = _connection.BeginTransaction(deferred: false);
            }
            catch (SqliteException ex) when (IsNestedTransactionError(ex))
            {
                RunReadMigrationStepsInsideExternalTransaction();
                return;
            }
            catch (InvalidOperationException ex) when (IsNestedTransactionError(ex))
            {
                RunReadMigrationStepsInsideExternalTransaction();
                return;
            }
            catch (SqliteException ex) when (IsReadOnlyOpenError(ex))
            {
                RecordMigrationFailure("BEGIN IMMEDIATE schema migration", ex);
                return;
            }

            using (transaction)
            {
                _activeMigrationTransaction = transaction;
                if (!RunReadMigrationSteps())
                    return;
                transaction.Commit();
            }

            _activeMigrationTransaction = null;

            EnsureForeignKeysEnabled();
        }
        finally
        {
            _activeMigrationTransaction = null;
            // Migration may have added columns or indexes the schema cache had already
            // resolved as missing; drop the cache so the next DbReader sees the new shape.
            // マイグレーションで列・index が追加された可能性があるためキャッシュを破棄する。
            _schemaCache?.Refresh();
        }
    }

    private void RunReadMigrationStepsInsideExternalTransaction()
    {
        _readMigrationInsideExternalTransaction = true;
        try
        {
            if (RunReadMigrationSteps())
                EnsureForeignKeysEnabled();
        }
        finally
        {
            _readMigrationInsideExternalTransaction = false;
        }
    }

    private bool RunReadMigrationSteps()
    {
        try
        {
            foreach (var (description, action) in BuildReadMigrationSteps())
            {
                try
                {
                    action();
                }
                catch (SqliteException ex)
                {
                    RecordMigrationFailure(description, ex);

                    // Read-only DB / filesystem / sandbox — stop further steps and degrade.
                    // Catches SQLITE_READONLY (8), SQLITE_IOERR (10), and SQLITE_CANTOPEN (14):
                    // some restricted environments report CANTOPEN when SQLite tries to create
                    // -journal side files for the DDL. DbReader.LoadColumns() / table-detection
                    // will drive the degraded read path; later read queries that hit a still-
                    // missing column will now have a single clear preceding diagnostic to refer to.
                    // 読み取り専用 DB・FS・サンドボックスでの DDL 失敗は縮退扱いで打ち切る。
                    if (IsReadOnlyOpenError(ex)) return false;

                    // Other SQLite errors (e.g. corruption, full disk) are not opportunistic-
                    // migration concerns — preserve the existing surface-the-exception behavior.
                    // それ以外の SQLite エラーは従来通り上位に伝播させる。
                    throw;
                }
            }

            return true;
        }
        finally
        {
            _activeMigrationTransaction = null;
        }
    }

    private static bool IsNestedTransactionError(SqliteException exception) =>
        exception.SqliteErrorCode == 1 &&
        exception.Message.Contains("cannot start a transaction within a transaction", StringComparison.OrdinalIgnoreCase);

    private static bool IsNestedTransactionError(InvalidOperationException exception) =>
        exception.Message.Contains("does not support nested transactions", StringComparison.OrdinalIgnoreCase);

    private void RecordMigrationFailure(string description, SqliteException exception)
    {
        var failure = new DbMigrationFailure(
            description,
            exception.SqliteErrorCode,
            FormatMigrationSqliteMessage(exception),
            BuildMigrationSuggestedAction(exception.SqliteErrorCode));
        LastMigrationFailure = failure;
        EmitMigrationFailureWarning(failure);
    }

    private IEnumerable<(string Description, Action Action)> BuildReadMigrationSteps()
    {
        // The order here matches the legacy inline migration: tables before the columns and
        // indexes that reference them, and fold columns before the folded indexes (#86).
        // 並び順は legacy インラインマイグレーションと同じ。テーブル→列→index、fold 列→folded index。
        yield return ("CREATE TABLE reference_lines", () => Execute(@"
            CREATE TABLE IF NOT EXISTS reference_lines (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id     INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                line        INTEGER NOT NULL,
                context     TEXT NOT NULL,
                UNIQUE(file_id, line, context)
            )"));
        yield return ("CREATE TABLE symbol_references", () => Execute(@"
            CREATE TABLE IF NOT EXISTS symbol_references (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                symbol_name     TEXT,
                reference_kind  TEXT,
                line            INTEGER,
                column_number   INTEGER,
                context         TEXT,
                reference_line_id INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL,
                container_kind  TEXT,
                container_name  TEXT,
                is_self_reference INTEGER NOT NULL DEFAULT 0,
                is_mutual_recursion INTEGER NOT NULL DEFAULT 0
            )"));
        yield return ("EnsureColumn symbol_references.reference_line_id",
            () => EnsureColumn("symbol_references", "reference_line_id", "INTEGER REFERENCES reference_lines(id) ON DELETE SET NULL"));
        yield return ("EnsureColumn symbol_references.is_self_reference",
            () => EnsureColumn("symbol_references", "is_self_reference", "INTEGER NOT NULL DEFAULT 0"));
        yield return ("EnsureColumn symbol_references.is_mutual_recursion",
            () => EnsureColumn("symbol_references", "is_mutual_recursion", "INTEGER NOT NULL DEFAULT 0"));
        yield return ("CREATE INDEX idx_symbol_refs_name",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name      ON symbol_references(symbol_name)"));
        yield return ("CREATE INDEX idx_symbol_refs_file",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_file      ON symbol_references(file_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_container",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container ON symbol_references(container_name)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_kind ON symbol_references(container_name, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_kind   ON symbol_references(symbol_name, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_file",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_file   ON symbol_references(symbol_name, file_id)"));
        yield return ("CREATE INDEX idx_reference_lines_file_line",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_reference_lines_file_line ON reference_lines(file_id, line)"));
        yield return ("CREATE INDEX idx_symbol_refs_reference_line",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_reference_line ON symbol_references(reference_line_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase      ON symbol_references(symbol_name COLLATE NOCASE)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase ON symbol_references(container_name COLLATE NOCASE)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_nocase_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_kind ON symbol_references(symbol_name COLLATE NOCASE, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_name_nocase_file",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_name_nocase_file ON symbol_references(symbol_name COLLATE NOCASE, file_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_nocase_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_nocase_kind ON symbol_references(container_name COLLATE NOCASE, reference_kind)"));

        yield return ("EnsureColumn files.checksum", () => EnsureColumn("files", "checksum", "TEXT"));
        yield return ("EnsureColumn files.modified", () => EnsureColumn("files", "modified", "DATETIME"));
        yield return ("EnsureColumn files.indexed_at", () => EnsureColumn("files", "indexed_at", "DATETIME"));
        yield return ("EnsureColumn symbols.start_line", () => EnsureColumn("symbols", "start_line", "INTEGER"));
        yield return ("EnsureColumn symbols.end_line", () => EnsureColumn("symbols", "end_line", "INTEGER"));
        yield return ("EnsureColumn symbols.body_start_line", () => EnsureColumn("symbols", "body_start_line", "INTEGER"));
        yield return ("EnsureColumn symbols.body_end_line", () => EnsureColumn("symbols", "body_end_line", "INTEGER"));
        yield return ("EnsureColumn symbols.signature", () => EnsureColumn("symbols", "signature", "TEXT"));
        yield return ("EnsureColumn symbols.container_kind", () => EnsureColumn("symbols", "container_kind", "TEXT"));
        yield return ("EnsureColumn symbols.container_name", () => EnsureColumn("symbols", "container_name", "TEXT"));
        yield return ("EnsureColumn symbols.container_qualified_name", () => EnsureColumn("symbols", "container_qualified_name", "TEXT"));
        yield return ("EnsureColumn symbols.family_key", () => EnsureColumn("symbols", "family_key", "TEXT"));
        yield return ("EnsureColumn symbols.visibility", () => EnsureColumn("symbols", "visibility", "TEXT"));
        yield return ("EnsureColumn symbols.return_type", () => EnsureColumn("symbols", "return_type", "TEXT"));
        yield return ("EnsureColumn symbols.is_metadata_target", () => EnsureColumn("symbols", "is_metadata_target", "INTEGER"));
        yield return ("EnsureColumn symbols.metadata_target_source", () => EnsureColumn("symbols", "metadata_target_source", "TEXT"));
        yield return ("CREATE INDEX idx_symbols_name_nocase",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_nocase ON symbols(name COLLATE NOCASE)"));

        // #86: fold columns must be ensured BEFORE the folded indexes so CREATE INDEX does
        // not fail on legacy DBs where the column did not exist yet.
        // #86: folded 列を追加してから folded index を作らないと legacy DB でクラッシュする。
        yield return ("EnsureColumn symbols.name_folded", () => EnsureColumn("symbols", "name_folded", "TEXT"));
        yield return ("EnsureColumn symbol_references.symbol_name_folded", () => EnsureColumn("symbol_references", "symbol_name_folded", "TEXT"));
        yield return ("EnsureColumn symbol_references.container_name_folded", () => EnsureColumn("symbol_references", "container_name_folded", "TEXT"));
        yield return ("CREATE INDEX idx_symbols_name_folded",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbols_name_folded                ON symbols(name_folded)"));
        yield return ("CREATE INDEX idx_symbol_refs_symbol_name_folded",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded     ON symbol_references(symbol_name_folded)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_name_folded",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded  ON symbol_references(container_name_folded)"));
        yield return ("CREATE INDEX idx_symbol_refs_symbol_name_folded_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_kind ON symbol_references(symbol_name_folded, reference_kind)"));
        yield return ("CREATE INDEX idx_symbol_refs_symbol_name_folded_file",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_symbol_name_folded_file ON symbol_references(symbol_name_folded, file_id)"));
        yield return ("CREATE INDEX idx_symbol_refs_container_name_folded_kind",
            () => Execute("CREATE INDEX IF NOT EXISTS idx_symbol_refs_container_name_folded_kind ON symbol_references(container_name_folded, reference_kind)"));

        yield return ("CREATE TABLE file_issues", () => Execute(@"
            CREATE TABLE IF NOT EXISTS file_issues (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                file_id         INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                kind            TEXT NOT NULL,
                line            INTEGER NOT NULL DEFAULT 0,
                message         TEXT NOT NULL
            )"));
        yield return ("CREATE TABLE codeindex_meta", () => Execute(@"
            CREATE TABLE IF NOT EXISTS codeindex_meta (
                key    TEXT PRIMARY KEY NOT NULL,
                value  TEXT
            )"));
    }

    private bool ReadMigrationSchemaIsCurrent()
    {
        foreach (var table in ReadMigrationRequiredTables)
        {
            if (!TableExists(table))
                return false;
        }

        foreach (var (table, column) in ReadMigrationRequiredColumns)
        {
            if (!ColumnExists(table, column))
                return false;
        }

        foreach (var index in ReadMigrationRequiredIndexes)
        {
            if (!IndexExists(index))
                return false;
        }

        return true;
    }

    private string BuildMigrationSuggestedAction(int sqliteErrorCode)
    {
        // 8 = SQLITE_READONLY, 10 = SQLITE_IOERR, 14 = SQLITE_CANTOPEN: classic restricted-
        // mount signatures (network share, sandbox, WORM). Point the user at the same fix
        // we already document for the read-only fallback so the message is actionable.
        // 8/10/14 は restricted mount 系の典型シグネチャ。書き込み可能な場所での再実行を案内する。
        if (sqliteErrorCode is 8 or 10 or 14)
        {
            return "Re-run cdidx on writable storage, or grant write access to <db directory> (for example, chmod +w <db directory>), so the schema migration can complete.";
        }

        // Unknown SQLite codes — surface the code itself and point at integrity check.
        // それ以外の SQLite エラーは integrity_check と error code を案内する。
        return $"Inspect the database with 'sqlite3 <db> \"PRAGMA integrity_check\"' (SQLite error code {sqliteErrorCode}).";
    }

    private static string FormatMigrationSqliteMessage(SqliteException exception)
        => DiagnosticRedactor.BoundDiagnosticText(
            DiagnosticRedactor.RedactSensitiveText(exception.Message, redactPaths: true),
            MigrationDiagnosticTextLimit);

    private static void EmitMigrationFailureWarning(DbMigrationFailure failure)
    {
        // Single line so the next read attempt only sees one clear "migration partial" record
        // even if multiple commands share the same process / log stream.
        // 1 行に集約し、後続 read エラーと混在しても拾いやすい形にする。
        CommandErrorWriter.WriteStderr(
            $"Warning: cdidx schema migration step \"{failure.Step}\" failed " +
            $"(SQLite error {failure.SqliteErrorCode}: {failure.SqliteMessage.TrimEnd('.')}). " +
            "Subsequent read queries may fail with 'no such column' until the migration completes. " +
            failure.SuggestedAction);
    }

    private void EnsureColumn(string tableName, string columnName, string definition)
    {
        var quotedTableName = SqliteIdentifier.Quote(tableName);
        var quotedColumnName = SqliteIdentifier.Quote(columnName);
        if (_activeMigrationTransaction != null || _readMigrationInsideExternalTransaction)
        {
            DbColumnEnsurer.EnsureColumn(
                () => ColumnExists(tableName, columnName),
                () => Execute($"ALTER TABLE {quotedTableName} ADD COLUMN {quotedColumnName} {definition}"));
            return;
        }

        DbColumnEnsurer.EnsureColumn(
            () => ColumnExists(tableName, columnName),
            beginImmediate: () => Execute("BEGIN IMMEDIATE"),
            commit: () => Execute("COMMIT"),
            rollback: () => Execute("ROLLBACK"),
            () => Execute($"ALTER TABLE {quotedTableName} ADD COLUMN {quotedColumnName} {definition}"));
    }

    private bool ColumnExists(string tableName, string columnName)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = SqliteCommandPolicy.TableInfoPragmaSql(tableName);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IndexExists(string name)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name";
        SqliteCommandPolicy.Add(cmd, "@name", name);
        return cmd.ExecuteScalar() != null;
    }

    private string ExecuteScalar(string sql)
    {
        using var cmd = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            cmd.Transaction = _activeMigrationTransaction;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private void NormalizeCodeIndexMetaKeys()
    {
        if (!TableExists("codeindex_meta"))
            return;

        using (var delete = SqliteConnectionPolicy.CreateCommand(_connection))
        {
            if (_activeMigrationTransaction != null)
                delete.Transaction = _activeMigrationTransaction;

            delete.CommandText = @"
                DELETE FROM codeindex_meta
                WHERE key IN ('hotspot_family_version', 'hotspot_family_marker_fingerprint')
                  AND value IS NULL";
            delete.ExecuteNonQuery();
        }

        using var stamp = SqliteConnectionPolicy.CreateCommand(_connection);
        if (_activeMigrationTransaction != null)
            stamp.Transaction = _activeMigrationTransaction;
        stamp.CommandText = @"
            INSERT INTO codeindex_meta (key, value) VALUES ('codeindex_meta_schema_version', @version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        SqliteCommandPolicy.Add(stamp, "@version", CodeIndexMetaSchemaVersion.ToString(CultureInfo.InvariantCulture));
        stamp.ExecuteNonQuery();
    }

    internal void MarkWriteWork(bool walCheckpointable = true)
    {
        if (!_isReadOnly && !_suppressWriteWorkTracking)
        {
            _hasWriteWork = true;
            if (walCheckpointable)
                _hasWalCheckpointableWriteWork = true;
        }
    }

    internal sealed record PlannerStatisticsMaintenanceFailure(string CommandText, SqliteException Exception);

    internal PlannerStatisticsMaintenanceFailure? RunPlannerStatisticsMaintenance(bool forceAnalyze)
    {
        if (_isReadOnly)
            return null;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = forceAnalyze ? "ANALYZE" : "PRAGMA optimize";
        try
        {
            PlannerStatisticsCommandCreatedForTesting?.Invoke(cmd);
            cmd.ExecuteNonQuery();
            PlannerStatisticsCommandExecutedForTesting?.Invoke(_connection.DataSource, cmd.CommandText);
            if (!forceAnalyze)
                OptimizePragmaExecutedForTesting?.Invoke(_connection.DataSource);
            _hasWriteWork = false;
            return null;
        }
        catch (SqliteException ex)
        {
            // Planner statistics are an index-performance aid. If SQLite rejects ANALYZE /
            // optimize during cleanup (read-only handoff, transient filesystem state), keep
            // the completed index usable instead of converting success into failure.
            return new PlannerStatisticsMaintenanceFailure(cmd.CommandText, ex);
        }
    }

    private void RunOptimizeOnCloseIfNeeded()
    {
        if (!_hasWriteWork || _isReadOnly)
            return;

        RunPlannerStatisticsMaintenance(forceAnalyze: false);
    }

    public void Dispose()
    {
        // Dispose cached prepared statements before closing the connection so each
        // SqliteCommand's finalizer does not race the connection teardown.
        // connection を閉じる前にキャッシュ済み command を dispose し、finalizer と
        // connection teardown の競合を防ぐ。
        _preparedCommands?.Dispose();
        _preparedCommands = null;
        var hadWriteWork = _hasWriteWork;
        var hadWalCheckpointableWriteWork = _hasWalCheckpointableWriteWork;
        RunOptimizeOnCloseIfNeeded();
        if (hadWalCheckpointableWriteWork)
            TryCheckpointWalTruncate();
        _connection.Dispose();
    }
}

/// <summary>
/// Captured information about a single failed step inside
/// <see cref="DbContext.TryMigrateForRead"/>. Surfaced via
/// <see cref="DbContext.LastMigrationFailure"/> so a later "no such column" error coming
/// out of a read path can be traced back to the specific step that did not run.
/// <see cref="DbContext.TryMigrateForRead"/> で失敗したステップの情報。
/// </summary>
public sealed record DbMigrationFailure(
    string Step,
    int SqliteErrorCode,
    string SqliteMessage,
    string SuggestedAction);

internal static class DbColumnEnsurer
{
    internal static void EnsureColumn(
        Func<bool> columnExists,
        Action? beginImmediate,
        Action? commit,
        Action? rollback,
        Action alterColumn)
    {
        if (columnExists())
            return;

        var hasTransactionHooks = beginImmediate != null && commit != null && rollback != null;
        var transactionStarted = false;
        try
        {
            if (hasTransactionHooks)
            {
                beginImmediate!();
                transactionStarted = true;
                if (columnExists())
                {
                    commit!();
                    transactionStarted = false;
                    return;
                }
            }

            alterColumn();
            if (transactionStarted)
            {
                commit!();
                transactionStarted = false;
            }
        }
        catch (SqliteException ex) when (IsDuplicateColumnRace(ex, columnExists))
        {
            // Another process or an earlier partial migration may have added the
            // column between PRAGMA inspection and ALTER. Re-check PRAGMA-derived
            // state and gate on SQLite's generic DDL error code so localized builds
            // or future wording changes still recover (#1532, #1690).
            // 列存在を PRAGMA 相当の状態で再確認し、SQLite の英語メッセージに依存せず
            // 「移行済み」を判定する (#1532)。
            if (transactionStarted)
            {
                try { rollback!(); } catch (SqliteException) { }
                transactionStarted = false;
            }
        }
        catch
        {
            if (transactionStarted)
            {
                try { rollback!(); } catch (SqliteException) { }
            }
            throw;
        }
    }

    internal static void EnsureColumn(Func<bool> columnExists, Action alterColumn)
        => EnsureColumn(columnExists, beginImmediate: null, commit: null, rollback: null, alterColumn);

    private static bool IsDuplicateColumnRace(SqliteException exception, Func<bool> columnExists)
    {
        if (!IsDuplicateColumnAddError(exception))
            return false;

        return columnExists();
    }

    private static bool IsDuplicateColumnAddError(SqliteException exception)
    {
        // SQLite reports duplicate-column ADD COLUMN as SQLITE_ERROR (1). Keep the
        // message fallback for older providers that may not surface the numeric code.
        return exception.SqliteErrorCode == 1 ||
               exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase);
    }
}
