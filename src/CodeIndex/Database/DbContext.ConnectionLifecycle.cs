using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
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
        _connection = OpenReadOnly(
            dbPath,
            out _readOnlyImmutableFallback,
            pooling: _connectionPooling);
        _immutableReadOnly = _readOnlyImmutableFallback;
        ApplyBusyTimeoutPragma();
        ApplyConnectionPerformancePragmas();
        RegisterConnectionFunctionsWithRetry(_connection, cancellationToken: cancellationToken);
        _isReadOnly = true;
        WarnIfBatchInProgress();
    }

    internal static WalCheckpointResult CheckpointWalBeforeReadOnlyFallback(
        string dbPath,
        CancellationToken cancellationToken,
        bool useConnectionPooling = true)
    {
        try
        {
            var mode = useConnectionPooling
                ? SqliteConnectionPolicyMode.ReadWrite
                : SqliteConnectionPolicyMode.ReadWriteUnpooled;
            var connectionString = SqliteConnectionPolicy.BuildConnectionString(dbPath, mode);
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
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
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
            cancellationToken.ThrowIfCancellationRequested();
            using var cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.UnsafeRegister(
                    static state => SQLitePCL.raw.sqlite3_interrupt(((SqliteConnection)state!).Handle),
                    connection)
                : default;
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
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("SQLite WAL checkpoint was interrupted.", ex, cancellationToken);
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

    internal FtsOptimizationRecommendation GetFtsOptimizationRecommendation()
    {
        using var transaction = _connection.BeginTransaction(deferred: true);
        var metadata = GetMetaStrings(
        [
            DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey,
            BatchInProgressMetaKey,
        ]);
        var incrementalWrites = long.TryParse(
            metadata[DbWriter.FtsIncrementalWritesSinceOptimizeMetaKey],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedWrites)
                ? parsedWrites
                : (long?)null;
        var batchInProgress = bool.TryParse(
            metadata[BatchInProgressMetaKey],
            out var parsedBatchInProgress)
                && parsedBatchInProgress;
        var userVersion = checked((int)ReadPragmaLong("user_version"));
        var indexNewerThanReader = DbReader.DetectNewerThanReaderContracts(
            _connection,
            userVersion).Newer;
        var recommendation = FtsOptimizationRecommendationEvaluator.Evaluate(
            new FtsOptimizationMetrics(
                incrementalWrites,
                ReadPragmaLong("page_count"),
                SnapshotCurrent: !batchInProgress
                    && !indexNewerThanReader
                    && !WalStaleSnapshotRisk));
        transaction.Commit();
        return recommendation;
    }

    public VacuumResult RunIncrementalVacuum(bool dryRun, CancellationToken cancellationToken)
    {
        _vacuumLogicalAfterDataVersion = null;
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
        var before = ReadVacuumMetricsInSnapshot(
            cancellationToken,
            stableDataVersion: out _);
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
        long? afterDataVersion = null;
        var after = dryRun
            ? before
            : ReadVacuumMetricsInSnapshot(
                cancellationToken,
                stableDataVersion: out afterDataVersion,
                afterFirstPragmaPhase: "metrics_after_page_count");
        _vacuumLogicalAfterDataVersion = afterDataVersion;
        cancellationToken.ThrowIfCancellationRequested();
        var pagesReclaimed = dryRun ? 0 : Math.Max(0, before.PageCount - after.PageCount);
        var bytesReclaimed = pagesReclaimed * after.PageSize;
        var estimatedPagesReclaimable = Math.Max(0, before.FreelistCount);
        var estimatedBytesReclaimable = estimatedPagesReclaimable * before.PageSize;
        var ftsOptimization = GetFtsOptimizationRecommendation();
        var guidance = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
            after.PageCount,
            after.FreelistCount,
            after.PageSize,
            after.WalSizeBytes,
            after.DbSizeBytes,
            after.AutoVacuumMode),
            ftsOptimization: ftsOptimization);
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
            LogicalDatabaseBytesBefore: before.PageCount * before.PageSize,
            LogicalDatabaseBytesAfter: after.PageCount * after.PageSize,
            MainFileBytesBefore: before.DbSizeBytes,
            MainFileBytesAfter: after.DbSizeBytes,
            WalFileBytesBefore: before.WalSizeBytes,
            WalFileBytesAfter: after.WalSizeBytes,
            ShmFileBytesBefore: before.ShmSizeBytes,
            ShmFileBytesAfter: after.ShmSizeBytes,
            PhysicalFileSetBytesBefore: before.PhysicalFileSetBytes,
            PhysicalFileSetBytesAfter: after.PhysicalFileSetBytes,
            WalCheckpointTimingNote: BuildWalCheckpointTimingNote(dryRun),
            AutoVacuumModeBefore: before.AutoVacuumMode,
            AutoVacuumModeBeforeName: MaintenanceGuidanceBuilder.FormatAutoVacuumMode(before.AutoVacuumMode) ?? "unknown",
            AutoVacuumModeAfter: after.AutoVacuumMode,
            AutoVacuumModeAfterName: MaintenanceGuidanceBuilder.FormatAutoVacuumMode(after.AutoVacuumMode) ?? "unknown",
            MaintenanceGuidance: guidance);
    }

    internal StatusRebuildReclaim RunRebuildReclaimIfRecommended(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        VacuumMetrics? before = null;
        StatusMaintenanceGuidance? guidanceBefore = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportMaintenanceProgress("rebuild_reclaim", "metrics_before", _connection.DataSource);
            var beforeMetrics = ReadVacuumMetrics(cancellationToken);
            before = beforeMetrics;
            guidanceBefore = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
                beforeMetrics.PageCount,
                beforeMetrics.FreelistCount,
                beforeMetrics.PageSize,
                beforeMetrics.WalSizeBytes,
                beforeMetrics.DbSizeBytes,
                beforeMetrics.AutoVacuumMode));

            if (guidanceBefore.FreelistState != "vacuum_recommended")
            {
                return BuildRebuildReclaimResult(
                    state: "not_needed",
                    reason: "freelist_below_threshold",
                    beforeMetrics,
                    beforeMetrics,
                    guidanceBefore,
                    started);
            }

            // Automatic rebuild maintenance must stay bounded to incremental auto-vacuum.
            // Legacy databases continue to use the explicit `cdidx vacuum` full-VACUUM path.
            // rebuild の自動 maintenance は incremental auto-vacuum に限定する。
            // legacy DB の full VACUUM は明示的な `cdidx vacuum` に残す。
            if (beforeMetrics.AutoVacuumMode != 2)
            {
                return BuildRebuildReclaimResult(
                    state: "skipped",
                    reason: "auto_vacuum_not_incremental",
                    beforeMetrics,
                    beforeMetrics,
                    guidanceBefore,
                    started);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ReportMaintenanceProgress("rebuild_reclaim", "incremental_vacuum", _connection.DataSource);
            Execute(DbPragmaPolicy.IncrementalVacuumPragmaSql(beforeMetrics.FreelistCount));
            cancellationToken.ThrowIfCancellationRequested();
            ReportMaintenanceProgress("rebuild_reclaim", "metrics_after", _connection.DataSource);
            var after = ReadVacuumMetrics(cancellationToken);
            var guidanceAfter = MaintenanceGuidanceBuilder.Build(new MaintenanceMetrics(
                after.PageCount,
                after.FreelistCount,
                after.PageSize,
                after.WalSizeBytes,
                after.DbSizeBytes,
                after.AutoVacuumMode));
            var completed = guidanceAfter.FreelistState != "vacuum_recommended";
            return BuildRebuildReclaimResult(
                state: completed ? "completed" : "incomplete",
                reason: completed ? "threshold_exceeded" : "freelist_still_above_threshold",
                beforeMetrics,
                after,
                guidanceBefore,
                started,
                guidanceAfter.FreelistRatio);
        }
        catch (Exception ex)
        {
            GlobalToolLog.Error("rebuild_reclaim_failed", ex, includeStacks: false);
            return BuildRebuildReclaimResult(
                state: ex is OperationCanceledException ? "cancelled" : "failed",
                reason: ClassifyRebuildReclaimFailure(ex),
                before,
                after: null,
                guidanceBefore,
                started);
        }
    }

    private static StatusRebuildReclaim BuildRebuildReclaimResult(
        string state,
        string reason,
        VacuumMetrics? before,
        VacuumMetrics? after,
        StatusMaintenanceGuidance? guidanceBefore,
        long startedTimestamp,
        double? freelistRatioAfter = null)
    {
        long? pagesReclaimed = before.HasValue && after.HasValue
            ? Math.Max(0, before.Value.PageCount - after.Value.PageCount)
            : null;
        var pageSize = after?.PageSize ?? before?.PageSize;
        return new StatusRebuildReclaim
        {
            State = state,
            Reason = reason,
            DurationMs = (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            PageSizeBytes = pageSize,
            PageCountBefore = before?.PageCount,
            FreelistCountBefore = before?.FreelistCount,
            FreelistRatioBefore = guidanceBefore?.FreelistRatio,
            FreelistThresholdRatio = guidanceBefore?.FreelistThresholdRatio,
            EstimatedBytesReclaimableBefore = guidanceBefore?.EstimatedBytesReclaimable,
            PageCountAfter = after?.PageCount,
            FreelistCountAfter = after?.FreelistCount,
            FreelistRatioAfter = after.HasValue
                ? freelistRatioAfter ?? guidanceBefore?.FreelistRatio
                : null,
            PagesReclaimed = pagesReclaimed,
            BytesReclaimed = pagesReclaimed.HasValue && pageSize.HasValue
                ? pagesReclaimed.Value * pageSize.Value
                : null,
            LogicalDatabaseBytesBefore = before.HasValue
                ? before.Value.PageCount * before.Value.PageSize
                : null,
            LogicalDatabaseBytesAfter = after.HasValue
                ? after.Value.PageCount * after.Value.PageSize
                : null,
            DbSizeBytesBefore = before?.DbSizeBytes,
            DbSizeBytesAfter = after?.DbSizeBytes,
            AutoVacuumMode = before?.AutoVacuumMode,
        };
    }

    private static string ClassifyRebuildReclaimFailure(Exception exception)
        => exception switch
        {
            OperationCanceledException => "cancelled",
            SqliteException { SqliteErrorCode: 5 } => "sqlite_busy",
            SqliteException { SqliteErrorCode: 6 } => "sqlite_locked",
            SqliteException { SqliteErrorCode: 8 } => "sqlite_read_only",
            SqliteException => "sqlite_error",
            UnauthorizedAccessException => "access_denied",
            IOException => "io_error",
            _ => "unexpected_error",
        };

    private static string? BuildWalCheckpointTimingNote(bool dryRun)
        => dryRun
            ? null
            : "wal_size_bytes_after is sampled before the vacuum connection closes; SQLite may checkpoint or truncate WAL pages after command cleanup, so a later status call can report a smaller wal_size_bytes value.";

    internal static VacuumResult FinalizeVacuumFileMetricsAfterConnectionClose(
        VacuumResult result,
        string dbPath,
        VacuumGenerationWitness? expectedWitness,
        CancellationToken cancellationToken = default)
    {
        VacuumFileSetMetrics after = default;
        if (expectedWitness.HasValue)
        {
            for (var attempt = 1; attempt <= VacuumFileSetCaptureMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadPostCloseVacuumMetrics(
                        dbPath,
                        cancellationToken,
                        out var observed))
                {
                    continue;
                }

                if (!MatchesVacuumLogicalAfter(result, observed))
                    break;
                if (!HasCompleteVacuumFileSet(observed))
                    continue;
                if (!IsCompatiblePostCloseVacuumGeneration(
                        expectedWitness.Value.SourceState,
                        observed.SourceState!.Value))
                {
                    break;
                }

                after = new VacuumFileSetMetrics(
                    observed.DbSizeBytes,
                    observed.WalSizeBytes,
                    observed.ShmSizeBytes,
                    observed.PhysicalFileSetBytes,
                    SourceState: null);
                break;
            }
        }

        var guidance = MaintenanceGuidanceBuilder.Build(
            new MaintenanceMetrics(
                result.PageCountAfter,
                result.FreelistCountAfter,
                result.PageSize,
                after.WalFileBytes,
                after.MainFileBytes,
                result.AutoVacuumModeAfter),
            ftsOptimization: result.MaintenanceGuidance.FtsOptimization);
        return result with
        {
            DbSizeBytesAfter = after.MainFileBytes,
            WalSizeBytesAfter = after.WalFileBytes,
            MainFileBytesAfter = after.MainFileBytes,
            WalFileBytesAfter = after.WalFileBytes,
            ShmFileBytesAfter = after.ShmFileBytes,
            PhysicalFileSetBytesAfter = after.PhysicalFileSetBytes,
            MaintenanceGuidance = guidance,
            WalCheckpointTimingNote = result.DryRun
                ? null
                : "File-size after fields are sampled from a stable observation after the command-owned vacuum connection closes. WAL and SHM can remain when another connection prevents checkpoint or truncation; unstable or inaccessible physical fields are omitted from CLI JSON.",
        };
    }

    private static bool TryReadPostCloseVacuumMetrics(
        string dbPath,
        CancellationToken cancellationToken,
        out VacuumMetrics metrics)
    {
        try
        {
            using var observer = CreateUnpooled(
                DbOpenIntent.QueryOnly,
                dbPath,
                cancellationToken);
            observer.SuppressPlannerStatisticsMaintenanceOnClose();
            using var transaction = observer.Connection.BeginTransaction(deferred: true);
            metrics = observer.ReadVacuumMetrics(
                cancellationToken,
                fileSetPhase: "post_close",
                fileSetMaxAttempts: 1);
            transaction.Commit();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CodeIndexException
            or SqliteException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException)
        {
            metrics = default;
            return false;
        }
    }

    private static bool MatchesVacuumLogicalAfter(
        VacuumResult result,
        VacuumMetrics observed)
        => observed.PageCount == result.PageCountAfter
           && observed.FreelistCount == result.FreelistCountAfter
           && observed.PageSize == result.PageSize
           && observed.AutoVacuumMode == result.AutoVacuumModeAfter;

    private static bool HasCompleteVacuumFileSet(VacuumMetrics metrics)
        => metrics.DbSizeBytes.HasValue
           && metrics.WalSizeBytes.HasValue
           && metrics.ShmSizeBytes.HasValue
           && metrics.PhysicalFileSetBytes.HasValue
           && metrics.SourceState.HasValue;

    private static bool IsCompatiblePostCloseVacuumGeneration(
        DbConnectionFactory.QueryOnlySnapshotSourceState expected,
        DbConnectionFactory.QueryOnlySnapshotSourceState observed)
    {
        if (SameVacuumGeneration(expected, observed))
            return true;

        // A reader can keep committed WAL frames alive through the pre-close witness.
        // A later checkpoint may fold those frames into the main file and remove the WAL.
        // A matching logical size signature keeps the public size metrics coherent across
        // that transition; other raw-generation changes fail closed.
        return expected.WalLength > 0 && observed.WalLength == 0;
    }

    private static bool SameVacuumGeneration(
        DbConnectionFactory.QueryOnlySnapshotSourceState expected,
        DbConnectionFactory.QueryOnlySnapshotSourceState observed)
    {
        if (observed.DbLength != expected.DbLength
            || observed.DbHeaderFingerprint != expected.DbHeaderFingerprint
            || observed.DatabaseFile != expected.DatabaseFile
            || observed.WalLength != expected.WalLength)
        {
            return false;
        }

        if (observed.WalLength == 0)
            return true;

        return observed.WalHeaderFingerprint == expected.WalHeaderFingerprint
            && observed.WalLastFrameFingerprint == expected.WalLastFrameFingerprint
            && observed.WalFile == expected.WalFile;
    }

    internal VacuumGenerationWitness? CaptureVacuumGenerationWitness(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_vacuumLogicalAfterDataVersion is not { } expectedDataVersion
            || ReadPragmaLong("data_version") != expectedDataVersion)
        {
            return null;
        }

        var fileSet = ReadVacuumFileSetMetrics(
            _connection.DataSource,
            "pre_close_generation",
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (fileSet.SourceState is not { } sourceState
            || ReadPragmaLong("data_version") != expectedDataVersion)
        {
            return null;
        }

        return new VacuumGenerationWitness(sourceState);
    }

    private static void ReportMaintenanceProgress(string operation, string phase, string dbPath)
    {
        GlobalToolLog.Info($"db_maintenance_progress operation={operation} phase={phase} db_path={ConsoleUi.FormatBoundedValue(dbPath)}");
        MaintenanceProgressForTesting?.Invoke(operation, phase);
    }

    private VacuumMetrics ReadVacuumMetrics(
        CancellationToken cancellationToken = default,
        string? fileSetPhase = null,
        int fileSetMaxAttempts = VacuumFileSetCaptureMaxAttempts,
        string? afterFirstPragmaPhase = null)
    {
        var pageCount = ReadPragmaLong("page_count");
        if (afterFirstPragmaPhase != null)
        {
            MaintenanceProgressForTesting?.Invoke("vacuum", afterFirstPragmaPhase);
            cancellationToken.ThrowIfCancellationRequested();
        }
        var freelistCount = ReadPragmaLong("freelist_count");
        var pageSize = ReadPragmaLong("page_size");
        var autoVacuumMode = ReadAutoVacuumMode();
        var fileSet = _queryOnlySnapshotSourcePath is { } sourcePath
            && _queryOnlySnapshotSourceState is { } sourceState
            ? ReadVacuumFileSetMetrics(
                sourcePath,
                fileSetPhase ?? "query_snapshot_source",
                cancellationToken,
                sourceState,
                fileSetMaxAttempts)
            : ReadVacuumFileSetMetrics(
                _connection.DataSource,
                fileSetPhase ?? "connection",
                cancellationToken,
                maxAttempts: fileSetMaxAttempts);
        return new(
            pageCount,
            freelistCount,
            pageSize,
            autoVacuumMode,
            fileSet.MainFileBytes,
            fileSet.WalFileBytes,
            fileSet.ShmFileBytes,
            fileSet.PhysicalFileSetBytes,
            fileSet.SourceState);
    }

    private VacuumMetrics ReadVacuumMetricsInSnapshot(
        CancellationToken cancellationToken,
        out long? stableDataVersion,
        string? afterFirstPragmaPhase = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dataVersionBefore = ReadPragmaLong("data_version");
        using var transaction = _connection.BeginTransaction(deferred: true);
        var metrics = ReadVacuumMetrics(
            cancellationToken,
            afterFirstPragmaPhase: afterFirstPragmaPhase);
        transaction.Commit();
        cancellationToken.ThrowIfCancellationRequested();
        var dataVersionAfter = ReadPragmaLong("data_version");
        stableDataVersion = dataVersionBefore == dataVersionAfter
            ? dataVersionAfter
            : null;
        if (!stableDataVersion.HasValue)
        {
            metrics = metrics with
            {
                DbSizeBytes = null,
                WalSizeBytes = null,
                ShmSizeBytes = null,
                PhysicalFileSetBytes = null,
                SourceState = null,
            };
        }

        return metrics;
    }

    private long ReadAutoVacuumMode() => ReadPragmaLong("auto_vacuum");

    private void ApplyBusyTimeoutPragma()
    {
        var busyTimeoutMs = DbPragmaPolicy.ReadBusyTimeoutMs(BusyTimeoutEnvironmentVariable);
        Execute(DbPragmaPolicy.BusyTimeoutPragmaSql(busyTimeoutMs));
    }

    private static VacuumFileSetMetrics ReadVacuumFileSetMetrics(
        string dbPath,
        string phase,
        CancellationToken cancellationToken,
        DbConnectionFactory.QueryOnlySnapshotSourceState? expectedSourceState = null,
        int maxAttempts = VacuumFileSetCaptureMaxAttempts)
    {
        var path = dbPath;
        if (string.IsNullOrWhiteSpace(path))
            return default;
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            path = DbConnectionFactory.TryGetLocalPath(path);
        if (string.IsNullOrWhiteSpace(path))
            return default;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DbConnectionFactory.TryCaptureQuerySourceState(
                    path,
                    cancellationToken,
                    out var sourceStateBefore)
                || (expectedSourceState is { } expectedBefore && sourceStateBefore != expectedBefore)
                || !TryReadVacuumFileSetState(path, out var fileSetBefore)
                || !FileSetMatchesSourceState(fileSetBefore, sourceStateBefore))
            {
                continue;
            }

            VacuumFileSetCaptureForTesting?.Invoke(phase, attempt, path);
            cancellationToken.ThrowIfCancellationRequested();
            if (!DbConnectionFactory.TryCaptureQuerySourceState(
                    path,
                    cancellationToken,
                    out var sourceStateAfter)
                || sourceStateBefore != sourceStateAfter
                || (expectedSourceState is { } expectedAfter && sourceStateAfter != expectedAfter)
                || !TryReadVacuumFileSetState(path, out var fileSetAfter)
                || fileSetBefore != fileSetAfter
                || !FileSetMatchesSourceState(fileSetAfter, sourceStateAfter))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var mainFileBytes = fileSetAfter.MainFile.Length;
            var walFileBytes = fileSetAfter.WalFile.Exists ? fileSetAfter.WalFile.Length : 0;
            var shmFileBytes = fileSetAfter.ShmFile.Exists ? fileSetAfter.ShmFile.Length : 0;
            return new VacuumFileSetMetrics(
                mainFileBytes,
                walFileBytes,
                shmFileBytes,
                TryAddNonNegative(mainFileBytes, walFileBytes, shmFileBytes),
                sourceStateAfter);
        }

        return default;
    }

    private static bool TryReadVacuumFileSetState(
        string dbPath,
        out VacuumFileSetState fileSet)
    {
        if (!TryReadVacuumFileState(dbPath, required: true, out var mainFile)
            || !TryReadVacuumFileState(dbPath + "-wal", required: false, out var walFile)
            || !TryReadVacuumFileState(dbPath + "-shm", required: false, out var shmFile))
        {
            fileSet = default;
            return false;
        }

        fileSet = new VacuumFileSetState(mainFile, walFile, shmFile);
        return true;
    }

    private static bool TryReadVacuumFileState(
        string path,
        bool required,
        out VacuumFileState state)
    {
        try
        {
            var normalizedPath = LongPath.EnsureWindowsPrefix(path);
            _ = VacuumFileMetadataProbeForTesting is { } metadataProbe
                ? metadataProbe(normalizedPath)
                : File.GetAttributes(normalizedPath);
            var info = new FileInfo(normalizedPath);
            info.Refresh();
            if (!info.Exists)
            {
                state = default;
                return false;
            }

            state = new VacuumFileState(
                true,
                info.Length,
                info.CreationTimeUtc.Ticks,
                info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            state = default;
            return !required;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or System.Security.SecurityException)
        {
            state = default;
            return false;
        }
    }

    private static bool FileSetMatchesSourceState(
        VacuumFileSetState fileSet,
        DbConnectionFactory.QueryOnlySnapshotSourceState sourceState)
    {
        if (!fileSet.MainFile.Exists
            || fileSet.MainFile.ToSourceIdentity() != sourceState.DatabaseFile)
        {
            return false;
        }

        return sourceState.WalFile is { } walFile
            ? fileSet.WalFile.Exists && fileSet.WalFile.ToSourceIdentity() == walFile
            : !fileSet.WalFile.Exists;
    }

    private static long? TryAddNonNegative(params long?[] values)
    {
        try
        {
            long total = 0;
            foreach (var value in values)
            {
                if (value is null or < 0)
                    return null;
                total = checked(total + value.Value);
            }

            return total;
        }
        catch (OverflowException)
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
        long? WalSizeBytes,
        long? ShmSizeBytes,
        long? PhysicalFileSetBytes,
        DbConnectionFactory.QueryOnlySnapshotSourceState? SourceState);

    private readonly record struct VacuumFileSetMetrics(
        long? MainFileBytes,
        long? WalFileBytes,
        long? ShmFileBytes,
        long? PhysicalFileSetBytes,
        DbConnectionFactory.QueryOnlySnapshotSourceState? SourceState);

    internal readonly record struct VacuumGenerationWitness(
        DbConnectionFactory.QueryOnlySnapshotSourceState SourceState);

    private readonly record struct VacuumFileSetState(
        VacuumFileState MainFile,
        VacuumFileState WalFile,
        VacuumFileState ShmFile);

    private readonly record struct VacuumFileState(
        bool Exists,
        long Length,
        long CreationTimeUtcTicks,
        long LastWriteTimeUtcTicks)
    {
        internal DbConnectionFactory.QuerySourceFileIdentity ToSourceIdentity()
            => new(Length, CreationTimeUtcTicks, LastWriteTimeUtcTicks);
    }

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

    private static SqliteConnection OpenReadOnly(
        string dbPath,
        out bool usedImmutableFallback,
        bool pooling = true)
        => DbConnectionFactory.OpenReadOnly(dbPath, out usedImmutableFallback, pooling);

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
