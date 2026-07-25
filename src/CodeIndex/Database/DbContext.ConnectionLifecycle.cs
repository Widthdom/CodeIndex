using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace CodeIndex.Database;

public partial class DbContext : IDisposable
{
    private void OpenQueryOnly(string dbPath, CancellationToken cancellationToken)
    {
        if (SqliteFileUri.StartsWithFileScheme(dbPath)
            && !SqliteFileUri.TryValidateBounds(dbPath, out var boundsError))
        {
            throw boundsError ?? new FormatException("Invalid SQLite file URI.");
        }

        try
        {
            var immutableSnapshot = false;
            var immutableWalRisk = false;
            var detachedSnapshot = false;
            DbConnectionFactory.QueryOnlySnapshotSourceState? snapshotSourceState = null;
            _connection = OpenSqliteConnectionWithRetry(
                () => DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
                    dbPath,
                    pooling: false,
                    out immutableSnapshot,
                    out immutableWalRisk,
                    out detachedSnapshot,
                    out snapshotSourceState,
                    cancellationToken),
                static connection => connection.Open(),
                dbPath: dbPath,
                cancellationToken: cancellationToken);
            Execute("PRAGMA query_only=ON");
            ApplyBusyTimeoutPragma();
            ApplyConnectionPerformancePragmas();
            RegisterConnectionFunctionsWithRetry(_connection, cancellationToken: cancellationToken);
            _isReadOnly = true;
            _immutableReadOnly = immutableSnapshot;
            _immutableReadOnlyWalRisk = immutableWalRisk;
            _connectionPooling = false;
            _queryOnlySnapshotRequiresRefresh = detachedSnapshot;
            _queryOnlySnapshotSourcePath = detachedSnapshot ? dbPath : null;
            _queryOnlySnapshotSourceState = snapshotSourceState;
            WarnIfBatchInProgress();
        }
        catch
        {
            _connection?.Dispose();
            throw;
        }
    }

    private void OpenReadOnlyFallback(string dbPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connection = OpenReadOnly(dbPath, out _readOnlyImmutableFallback);
        _immutableReadOnly = _readOnlyImmutableFallback;
        ApplyBusyTimeoutPragma();
        ApplyConnectionPerformancePragmas();
        RegisterConnectionFunctionsWithRetry(_connection, cancellationToken: cancellationToken);
        _isReadOnly = true;
        WarnIfBatchInProgress();
    }

    internal static WalCheckpointResult CheckpointWalBeforeReadOnlyFallback(
        string dbPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadWrite);
            using var connection = OpenSqliteConnectionWithRetry(
                () => new SqliteConnection(connectionString),
                static connection => connection.Open(),
                maxOpenAttempts: 1,
                dbPath: dbPath,
                cancellationToken: cancellationToken);
            return ExecuteWalCheckpointTruncate(connection, cancellationToken, invokeTestingHook: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WalCheckpointResult.Failed(FormatWalCheckpointFailureReason(ex));
        }
    }

    private static string FormatWalCheckpointFailureReason(Exception ex) => ex switch
    {
        SqliteException { SqliteErrorCode: 3 } => "sqlite_permission_denied",
        SqliteException { SqliteErrorCode: 5 } => "sqlite_busy",
        SqliteException { SqliteErrorCode: 6 } => "sqlite_locked",
        SqliteException { SqliteErrorCode: 8 } => "sqlite_read_only",
        SqliteException { SqliteErrorCode: 10 } => "sqlite_io_error",
        SqliteException { SqliteErrorCode: 11 } => "sqlite_corrupt",
        SqliteException { SqliteErrorCode: 13 } => "sqlite_full",
        SqliteException { SqliteErrorCode: 14 } => "sqlite_cannot_open",
        SqliteException { SqliteErrorCode: 26 } => "sqlite_not_a_database",
        SqliteException sqlite => $"sqlite_error_{sqlite.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)}",
        CodeIndexException codeIndexException => codeIndexException.Code,
        _ => WalCheckpointResult.GenericFailureReason,
    };

    public bool TryCheckpointWalTruncate()
        => TryCheckpointWalTruncate(CancellationToken.None);

    public bool TryCheckpointWalTruncate(CancellationToken cancellationToken)
        => CheckpointWalTruncate(cancellationToken).Succeeded;

    public WalCheckpointResult CheckpointWalTruncate()
        => CheckpointWalTruncate(CancellationToken.None);

    public WalCheckpointResult CheckpointWalTruncate(CancellationToken cancellationToken)
    {
        if (_isReadOnly)
        {
            var result = WalCheckpointResult.NotAttempted(WalCheckpointResult.ReadOnlySkippedReason);
            ApplyWalCheckpointResult(result);
            return result;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = ExecuteWalCheckpointTruncate(_connection, cancellationToken, invokeTestingHook: true);
            ApplyWalCheckpointResult(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            ApplyWalCheckpointResult(WalCheckpointResult.Failed(WalCheckpointResult.CancelledFailureReason));
            throw;
        }
    }

    private static WalCheckpointResult ExecuteWalCheckpointTruncate(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        bool invokeTestingHook)
    {
        try
        {
            using var cmd = SqliteConnectionPolicy.CreateCommand(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            if (invokeTestingHook)
                WalCheckpointTruncateExecutedForTesting?.Invoke(connection.DataSource);

            cancellationToken.ThrowIfCancellationRequested();
            ReportMaintenanceProgress("wal_checkpoint", "truncate_start", connection.DataSource);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return WalCheckpointResult.Failed(WalCheckpointResult.MissingResultFailureReason);

            long busy;
            long logPageCount;
            long checkpointedPageCount;
            try
            {
                busy = reader.GetInt64(0);
                logPageCount = reader.GetInt64(1);
                checkpointedPageCount = reader.GetInt64(2);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException
                or InvalidCastException
                or InvalidOperationException
                or IndexOutOfRangeException)
            {
                return WalCheckpointResult.Failed(WalCheckpointResult.InvalidResultFailureReason);
            }

            ReportMaintenanceProgress("wal_checkpoint", "truncate_complete", connection.DataSource);
            cancellationToken.ThrowIfCancellationRequested();

            var notWalMode = busy == 0 && logPageCount == -1 && checkpointedPageCount == -1;
            if (!notWalMode &&
                (busy < 0 || logPageCount < 0 || checkpointedPageCount < 0 || checkpointedPageCount > logPageCount))
            {
                return new WalCheckpointResult(
                    true,
                    false,
                    busy,
                    logPageCount,
                    checkpointedPageCount,
                    null,
                    null,
                    WalCheckpointResult.InvalidResultFailureReason);
            }

            var remainingPageCount = notWalMode ? 0 : logPageCount - checkpointedPageCount;
            var failureReason = busy != 0
                ? WalCheckpointResult.BusyFailureReason
                : remainingPageCount != 0
                    ? WalCheckpointResult.PagesRemainingFailureReason
                    : null;

            return new WalCheckpointResult(
                true,
                failureReason == null,
                busy,
                logPageCount,
                checkpointedPageCount,
                remainingPageCount,
                null,
                failureReason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return WalCheckpointResult.Failed(FormatWalCheckpointFailureReason(ex));
        }
    }

    private void ApplyWalCheckpointResult(WalCheckpointResult result)
    {
        _walCheckpointAttempted = result.Attempted;
        _walCheckpointSucceeded = result.Succeeded;
        _walCheckpointBusy = result.Busy;
        _walCheckpointLogPageCount = result.LogPageCount;
        _walCheckpointCheckpointedPageCount = result.CheckpointedPageCount;
        _walCheckpointRemainingPageCount = result.RemainingPageCount;
        _walCheckpointSkippedReason = result.SkippedReason;
        _walCheckpointFailureReason = result.FailureReason;
    }

    public static string ToReadOnlyUri(string dbPath)
        => SqliteConnectionPolicy.ToReadOnlyUri(dbPath);

    private void ApplyPrivateDatabaseFileModes(string dbPath)
    {
        if (!_databaseFileModeProvider.SupportsUnixFileModes ||
            dbPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyPrivateFileModeIfExists(dbPath, "database");
        ApplyPrivateFileModeIfExists(dbPath + "-wal", "wal");
        ApplyPrivateFileModeIfExists(dbPath + "-shm", "shm");
    }

    private void ApplyPrivateFileModeIfExists(string path, string target)
    {
        var normalizedPath = LongPath.EnsureWindowsPrefix(path);
        try
        {
            if (!_databaseFileModeProvider.FileExists(normalizedPath))
                return;

#pragma warning disable CA1416
            _databaseFileModeProvider.SetUnixFileMode(
                normalizedPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            HandleDatabasePermissionFailure("set", target, ex);
        }
    }

    public static string? GetUnixFileModeString(string? path)
        => GetUnixFileModeString(
            path,
            DatabasePermissionPolicyMode.BestEffort,
            SystemDatabaseFileModeProvider.Instance,
            out _);

    internal static string? GetUnixFileModeString(
        string? path,
        string policyName,
        out StatusDatabasePermissionDiagnostic? diagnostic)
        => GetUnixFileModeString(
            path,
            string.Equals(policyName, DatabasePermissionPolicy.StrictName, StringComparison.Ordinal)
                ? DatabasePermissionPolicyMode.Strict
                : DatabasePermissionPolicyMode.BestEffort,
            SystemDatabaseFileModeProvider.Instance,
            out diagnostic);

    internal static string? GetUnixFileModeString(
        string? path,
        DatabasePermissionPolicyMode policy,
        IDatabaseFileModeProvider fileModeProvider,
        out StatusDatabasePermissionDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(path) ||
            !fileModeProvider.SupportsUnixFileModes ||
            path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            if (!fileModeProvider.FileExists(path))
                return null;

            var mode = fileModeProvider.GetUnixFileMode(path) &
                (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                 UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                 UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
            return Convert.ToString((int)mode, 8).PadLeft(4, '0');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            diagnostic = DatabasePermissionPolicy.CreateDiagnostic("read", "database", ex);
            if (policy == DatabasePermissionPolicyMode.Strict)
                throw DatabasePermissionPolicy.CreateStrictFailure(diagnostic, ex);

            WriteBestEffortDatabasePermissionWarning(diagnostic);
            return null;
        }
    }

    private void HandleDatabasePermissionFailure(string operation, string target, Exception exception)
    {
        var diagnostic = DatabasePermissionPolicy.CreateDiagnostic(operation, target, exception);
        if (_databasePermissionPolicy == DatabasePermissionPolicyMode.Strict)
            throw DatabasePermissionPolicy.CreateStrictFailure(diagnostic, exception);

        if (_databasePermissionDiagnostics.Any(existing =>
                existing.Operation == diagnostic.Operation &&
                existing.Target == diagnostic.Target &&
                existing.Reason == diagnostic.Reason))
        {
            return;
        }

        _databasePermissionDiagnostics.Add(diagnostic);
        WriteBestEffortDatabasePermissionWarning(diagnostic);
    }

    private static void WriteBestEffortDatabasePermissionWarning(StatusDatabasePermissionDiagnostic diagnostic)
        => CommandErrorWriter.WriteStderr(
            $"Warning [{DatabasePermissionPolicy.FailureCode}]: policy={DatabasePermissionPolicy.BestEffortName} "
            + $"operation={diagnostic.Operation} target={diagnostic.Target} reason={diagnostic.Reason}; "
            + diagnostic.RecommendedAction);

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
            CommandErrorWriter.WriteStderr("Warning: Last batch did not complete; run `cdidx index --rebuild` to re-index from a known clean state.");
    }

    /// <summary>
    /// Demote readiness after an interrupted batch only from an explicitly selected repair path.
    /// interrupted batch 後の readiness demotion は、明示的な repair path からのみ実行する。
    /// </summary>
    public bool RepairIncompleteBatchReadiness()
    {
        if (_openIntent != DbOpenIntent.Repair)
            throw new InvalidOperationException("Incomplete-batch readiness repair requires DbOpenIntent.Repair.");

        var raw = GetMetaString(BatchInProgressMetaKey);
        if (!string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase))
            return false;

        ClearReadyFlags();
        return true;
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
            Execute(DbPragmaPolicy.AutoVacuumIncrementalPragmaSql);
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
            Execute(DbPragmaPolicy.IncrementalVacuumPragmaSql(before.FreelistCount));
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
            WalCheckpointTimingNote: BuildWalCheckpointTimingNote(dryRun),
            AutoVacuumModeBefore: before.AutoVacuumMode,
            AutoVacuumModeBeforeName: MaintenanceGuidanceBuilder.FormatAutoVacuumMode(before.AutoVacuumMode) ?? "unknown",
            AutoVacuumModeAfter: after.AutoVacuumMode,
            AutoVacuumModeAfterName: MaintenanceGuidanceBuilder.FormatAutoVacuumMode(after.AutoVacuumMode) ?? "unknown",
            MaintenanceGuidance: guidance);
    }

    private static string? BuildWalCheckpointTimingNote(bool dryRun)
        => dryRun
            ? null
            : "wal_size_bytes_after is sampled before the vacuum connection closes; SQLite may checkpoint or truncate WAL pages after command cleanup, so a later status call can report a smaller wal_size_bytes value.";

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
        Execute(DbPragmaPolicy.BusyTimeoutPragmaSql(busyTimeoutMs));
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

    private static bool IsReadOnlyOpenError(SqliteException ex, string dbPath) =>
        DbConnectionFactory.IsReadOnlyOpenError(ex, dbPath);

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

    private static SqliteConnection CreateArtifactPreservingQueryOnlyConnection(
        string dbPath,
        bool pooling,
        out bool immutableSnapshot,
        out bool immutableWalRisk,
        out bool detachedSnapshot)
        => DbConnectionFactory.CreateArtifactPreservingQueryOnlyConnection(
            dbPath,
            pooling,
            out immutableSnapshot,
            out immutableWalRisk,
            out detachedSnapshot);

}
