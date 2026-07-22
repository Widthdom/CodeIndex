using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private static readonly long? CurrentProcessStartTimeUtcTicks = TryGetCurrentProcessStartTimeUtcTicks();
    private static readonly Guid CurrentProcessIncarnationToken = Guid.NewGuid();
    internal const string FtsBulkLoadGenerationClearInsertTriggerName = "codeindex_fts_bulk_generation_clear_insert";
    internal const string FtsBulkLoadGenerationClearUpdateTriggerName = "codeindex_fts_bulk_generation_clear_update";
    internal const string FtsBulkLoadGenerationClearDeleteTriggerName = "codeindex_fts_bulk_generation_clear_delete";
    internal const string FtsDropTriggersMaintenancePhase = "drop_triggers";
    internal const string FtsRestoreTriggersMaintenancePhase = "restore_triggers";
    internal const string FtsRebuildMaintenancePhase = "rebuild";
    private static readonly AsyncLocal<Action<string>?> ScopedFtsMaintenanceBeforeExecuteForTesting = new();
    internal static Action<string>? FtsMaintenanceBeforeExecuteForTesting
    {
        get => ScopedFtsMaintenanceBeforeExecuteForTesting.Value;
        set => ScopedFtsMaintenanceBeforeExecuteForTesting.Value = value;
    }

    /// <summary>
    /// Optimize FTS5 index to merge internal b-tree segments for better query performance.
    /// FTS5インデックスを最適化して内部b-treeセグメントを統合し、クエリ性能を改善する。
    /// </summary>
    public void OptimizeFts(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        Execute("INSERT INTO fts_chunks(fts_chunks) VALUES('optimize')", cancellationToken);
        stopwatch.Stop();
        SetMetaValues(
            (FtsIncrementalWritesSinceOptimizeMetaKey, "0"),
            (FtsIncrementalWritesSinceMergeMetaKey, "0"),
            (FtsLastOptimizedAtMetaKey, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            (FtsLastOptimizeDurationMsMetaKey, stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Run an FTS5 incremental merge with a minimum merged-page work target.
    /// SQLite processes complete segments, so the actual page count can exceed the target.
    /// FTS5 の incremental merge を、merge page 数の最小 work target 付きで実行する。
    /// SQLite は segment 単位で処理するため、実際の page 数は target を超えることがある。
    /// </summary>
    public void MergeFtsSegments(
        int workTargetPages = DefaultFtsIncrementalMergeWorkTargetPages,
        CancellationToken cancellationToken = default)
    {
        if (workTargetPages <= 0)
            throw new ArgumentOutOfRangeException(nameof(workTargetPages));

        cancellationToken.ThrowIfCancellationRequested();
        // A negative target continues across levels until at least its absolute value in
        // merged pages is written. Segment granularity may exceed that target.
        // 負の target は絶対値以上の merge page を書くまで level をまたぐ。
        // segment 単位で処理するため target を超える場合がある。
        var sql = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"INSERT INTO fts_chunks(fts_chunks, rank) VALUES('merge', {-workTargetPages})");
        Execute(sql, cancellationToken);

        SetMeta(FtsIncrementalWritesSinceMergeMetaKey, "0");
    }

    /// <summary>
    /// Temporarily disable per-row FTS synchronization before a full bulk rewrite.
    /// The caller must restore the triggers and rebuild FTS from `chunks` before commit.
    /// full bulk rewrite の前に、行ごとの FTS 同期を一時停止する。
    /// caller は commit 前に trigger 復元と chunks からの FTS rebuild を行う。
    /// </summary>
    public void SuspendFtsSyncTriggersForBulkLoad()
    {
        EnsureFtsBulkLoadGenerationCleanupTriggers();
        SetFtsBulkLoadOwnerMarker();
        try
        {
            FtsMaintenanceBeforeExecuteForTesting?.Invoke(FtsDropTriggersMaintenancePhase);
            Execute(DbContext.DropFtsChunksSyncTriggersSql);
            _markWriteWork?.Invoke();
        }
        catch
        {
            // Guard construction has not completed yet, so Dispose cannot repair a partial
            // trigger drop. Make the established marker recoverable by this same process and
            // preserve the original suspension failure even if the marker rewrite also fails.
            // guard construction 前の partial trigger drop は Dispose で修復できない。同一
            // process の後続 recovery が動ける marker へ落とし、marker 再書込の失敗でも
            // 元の suspend failure を隠さない。
            try
            {
                MarkFtsBulkLoadRecoveryNeeded();
            }
            catch
            {
                // The original trigger-suspension failure is the actionable error.
            }
            throw;
        }
    }

    /// <summary>
    /// Restore FTS synchronization triggers after a bulk load.
    /// bulk load 後に FTS 同期 trigger を復元する。
    /// </summary>
    public void RestoreFtsSyncTriggers(CancellationToken cancellationToken = default)
    {
        FtsMaintenanceBeforeExecuteForTesting?.Invoke(FtsRestoreTriggersMaintenancePhase);
        Execute(DbContext.CreateFtsChunksSyncTriggersSql, cancellationToken);
        _markWriteWork?.Invoke();
    }

    /// <summary>
    /// Rebuild the external-content FTS table from the current chunks table.
    /// 現在の chunks テーブルから external-content FTS テーブルを再構築する。
    /// </summary>
    public void RebuildFtsFromChunks(
        bool resetIncrementalWriteCounter = true,
        CancellationToken cancellationToken = default)
    {
        FtsMaintenanceBeforeExecuteForTesting?.Invoke(FtsRebuildMaintenancePhase);
        Execute("INSERT INTO fts_chunks(fts_chunks) VALUES('rebuild')", cancellationToken);
        if (resetIncrementalWriteCounter)
        {
            SetMetaValues(
                (FtsIncrementalWritesSinceOptimizeMetaKey, "0"),
                (FtsIncrementalWritesSinceMergeMetaKey, "0"));
        }
    }

    public void ClearFtsBulkLoadInProgress()
        => SetMetaValues(
            (FtsBulkLoadInProgressMetaKey, null),
            (FtsBulkLoadOwnerGenerationMetaKey, null));

    internal void MarkFtsBulkLoadRecoveryNeeded()
        => SetMetaValues(
            (FtsBulkLoadInProgressMetaKey, "true"),
            (FtsBulkLoadOwnerGenerationMetaKey, null));

    private void SetFtsBulkLoadOwnerMarker()
    {
        if (!HasMetaTable())
            return;

        // Keep the legacy-readable primary marker and its generation association atomic while
        // guaranteeing that the primary-row cleanup trigger runs before the generation write.
        // legacy reader が読める primary marker と generation の関連付けを atomic に保ち、
        // primary-row cleanup trigger が generation write より先に動く順序も保証する。
        Execute("SAVEPOINT set_fts_bulk_load_owner_atomic");
        try
        {
            SetMetaCore(FtsBulkLoadInProgressMetaKey, CreateFtsBulkLoadMarker());
            SetMetaCore(FtsBulkLoadOwnerGenerationMetaKey, CreateFtsBulkLoadOwnerGeneration());
            Execute("RELEASE SAVEPOINT set_fts_bulk_load_owner_atomic");
        }
        catch
        {
            try { Execute("ROLLBACK TO SAVEPOINT set_fts_bulk_load_owner_atomic"); }
            catch (SqliteException) { /* best effort */ }
            try { Execute("RELEASE SAVEPOINT set_fts_bulk_load_owner_atomic"); }
            catch (SqliteException) { /* best effort */ }
            throw;
        }
    }

    public bool RecoverInterruptedFtsBulkLoadIfNeeded(CancellationToken cancellationToken = default)
    {
        var ownerState = ReadFtsBulkLoadOwnerState(cancellationToken);
        var marker = ownerState.Marker;
        var ownerGeneration = ownerState.OwnerGeneration;
        var trustedOwnerGeneration = ownerGeneration != null
            && ownerState.HasGenerationCleanupTriggers
                ? ownerGeneration
                : null;
        if (!IsFtsBulkLoadMarkerSet(marker)
            || IsFtsBulkLoadOwnerActive(marker!, trustedOwnerGeneration))
            return false;

        try
        {
            RestoreFtsSyncTriggers(cancellationToken);
            RebuildFtsFromChunks(cancellationToken: cancellationToken);
            ClearFtsBulkLoadInProgress();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // CREATE TRIGGER is idempotent and short. Restore source synchronization without
            // cancellation, then retain an owner-independent marker so the next run can rebuild.
            // CREATE TRIGGER は冪等で短い。cancel なしで source 同期を復元し、次回 run が
            // rebuild できるよう owner 非依存 marker を残す。
            RestoreFtsSyncTriggers();
            MarkFtsBulkLoadRecoveryNeeded();
            throw;
        }
    }

    private static string CreateFtsBulkLoadMarker()
        => "pid:" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string CreateFtsBulkLoadOwnerGeneration()
    {
        var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return CurrentProcessStartTimeUtcTicks is long startTimeUtcTicks
            ? "pid:" + pid + ":start:"
                + startTimeUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"pid:{pid}:token:{CurrentProcessIncarnationToken:N}";
    }

    private static bool IsFtsBulkLoadMarkerSet(string? marker)
        => string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase)
           || TryGetFtsBulkLoadOwnerPid(marker, out _);

    private static bool IsFtsBulkLoadOwnerActive(string marker, string? ownerGeneration)
    {
        if (!TryGetFtsBulkLoadOwnerPid(marker, out var pid))
            return false;

        var hasAssociatedOwnerGeneration = TryGetFtsBulkLoadOwnerGeneration(
            ownerGeneration,
            out var generationPid,
            out var expectedStartTimeUtcTicks,
            out var expectedIncarnationToken)
            && generationPid == pid;
        if (!hasAssociatedOwnerGeneration)
        {
            // Missing, malformed, or mismatched generation metadata may have been written by an
            // older binary. Treat the primary PID-only marker conservatively in that case.
            // generation metadata がない、壊れている、または PID が一致しない場合は旧 binary
            // 由来の primary PID-only marker として保守的に扱う。
            expectedStartTimeUtcTicks = null;
            expectedIncarnationToken = null;
        }

        if (pid == Environment.ProcessId)
            return IsFtsBulkLoadOwnerGenerationMatch(
                expectedStartTimeUtcTicks,
                expectedIncarnationToken,
                CurrentProcessStartTimeUtcTicks,
                CurrentProcessIncarnationToken);

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;
            if (expectedStartTimeUtcTicks == null)
                return true;

            return TryGetProcessStartTimeUtcTicks(process, out var actualStartTimeUtcTicks)
                ? expectedStartTimeUtcTicks == actualStartTimeUtcTicks
                : true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // If the platform cannot expose another process's generation, assume it is still
            // the owner. A delayed recovery is safer than rebuilding beneath a live bulk writer.
            // 他 process の generation を取得できない platform では active とみなす。
            // live bulk writer の途中で rebuild するより recovery 遅延の方が安全。
            return true;
        }
    }

    private void EnsureFtsBulkLoadGenerationCleanupTriggers()
    {
        Execute($"""
            CREATE TRIGGER IF NOT EXISTS {FtsBulkLoadGenerationClearInsertTriggerName}
            AFTER INSERT ON codeindex_meta
            WHEN NEW.key = '{FtsBulkLoadInProgressMetaKey}'
            BEGIN
                UPDATE codeindex_meta
                SET value = NULL
                WHERE key = '{FtsBulkLoadOwnerGenerationMetaKey}';
            END
            """);
        Execute($"""
            CREATE TRIGGER IF NOT EXISTS {FtsBulkLoadGenerationClearUpdateTriggerName}
            AFTER UPDATE ON codeindex_meta
            WHEN OLD.key = '{FtsBulkLoadInProgressMetaKey}'
              OR NEW.key = '{FtsBulkLoadInProgressMetaKey}'
            BEGIN
                UPDATE codeindex_meta
                SET value = NULL
                WHERE key = '{FtsBulkLoadOwnerGenerationMetaKey}';
            END
            """);
        Execute($"""
            CREATE TRIGGER IF NOT EXISTS {FtsBulkLoadGenerationClearDeleteTriggerName}
            AFTER DELETE ON codeindex_meta
            WHEN OLD.key = '{FtsBulkLoadInProgressMetaKey}'
            BEGIN
                UPDATE codeindex_meta
                SET value = NULL
                WHERE key = '{FtsBulkLoadOwnerGenerationMetaKey}';
            END
            """);
    }

    private (string? Marker, string? OwnerGeneration, bool HasGenerationCleanupTriggers)
        ReadFtsBulkLoadOwnerState(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasMetaTable())
            return (null, null, false);

        try
        {
            using var command = _conn.CreateCommand();
            command.Transaction = _activeTransaction;
            command.CommandText = $"""
                SELECT
                    (SELECT value
                     FROM codeindex_meta
                     WHERE key = '{FtsBulkLoadInProgressMetaKey}'),
                    (SELECT value
                     FROM codeindex_meta
                     WHERE key = '{FtsBulkLoadOwnerGenerationMetaKey}'),
                    (SELECT COUNT(*)
                     FROM sqlite_master
                     WHERE type = 'trigger'
                       AND name IN (
                           '{FtsBulkLoadGenerationClearInsertTriggerName}',
                           '{FtsBulkLoadGenerationClearUpdateTriggerName}',
                           '{FtsBulkLoadGenerationClearDeleteTriggerName}'))
                """;
            using var cancellationRegistration = RegisterSqliteInterrupt(cancellationToken);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("FTS bulk-load owner state query returned no row.");

            // Match GetMetaString's legacy behavior: a malformed non-text value is treated as
            // absent rather than turning best-effort startup recovery into a cast failure.
            // GetMetaString と同様、壊れた non-text value は cast failure ではなく欠落扱い。
            var marker = reader.GetValue(0) as string;
            var ownerGeneration = reader.GetValue(1) as string;
            var generationCleanupTriggerCount = reader.GetInt64(2);
            cancellationToken.ThrowIfCancellationRequested();
            return (marker, ownerGeneration, generationCleanupTriggerCount == 3);
        }
        catch (SqliteException ex) when (IsSqliteInterruptCancellation(ex, cancellationToken))
        {
            throw new OperationCanceledException(
                "FTS bulk-load owner generation preflight was interrupted.",
                ex,
                cancellationToken);
        }
    }

    internal static bool IsFtsBulkLoadOwnerGenerationMatch(
        long? expectedStartTimeUtcTicks,
        Guid? expectedIncarnationToken,
        long? currentProcessStartTimeUtcTicks,
        Guid currentProcessIncarnationToken)
    {
        // An incarnation that cannot read its own start time emits token markers. Therefore a
        // start-time marker for the same PID cannot belong to that incarnation and must not
        // suppress recovery after PID reuse.
        // 自身の start time を取得できない incarnation は token marker を書く。同一 PID の
        // start-time marker はその incarnation のものではないため recovery を抑止しない。
        if (expectedStartTimeUtcTicks != null)
            return expectedStartTimeUtcTicks == currentProcessStartTimeUtcTicks;

        return expectedIncarnationToken == null
            || expectedIncarnationToken == currentProcessIncarnationToken;
    }

    private static bool TryGetFtsBulkLoadOwnerPid(string? marker, out int pid)
    {
        const string prefix = "pid:";
        pid = 0;
        if (marker == null || !marker.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return int.TryParse(
            marker.AsSpan(prefix.Length),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out pid)
            && pid > 0;
    }

    private static bool TryGetFtsBulkLoadOwnerGeneration(
        string? generation,
        out int pid,
        out long? startTimeUtcTicks,
        out Guid? incarnationToken)
    {
        pid = 0;
        startTimeUtcTicks = null;
        incarnationToken = null;
        if (generation == null)
            return false;

        var generationSpan = generation.AsSpan();
        const string pidPrefix = "pid:";
        if (!generationSpan.StartsWith(pidPrefix, StringComparison.Ordinal))
            return false;

        var pidSuffix = generationSpan[pidPrefix.Length..];
        var pidSeparatorIndex = pidSuffix.IndexOf(':');
        if (pidSeparatorIndex <= 0
            || !int.TryParse(
                pidSuffix[..pidSeparatorIndex],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out pid)
            || pid <= 0)
        {
            pid = 0;
            return false;
        }

        generationSpan = pidSuffix[(pidSeparatorIndex + 1)..];
        const string startPrefix = "start:";
        if (generationSpan.StartsWith(startPrefix, StringComparison.Ordinal))
        {
            if (!long.TryParse(
                generationSpan[startPrefix.Length..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedStartTimeUtcTicks)
                || parsedStartTimeUtcTicks <= 0)
            {
                return false;
            }

            startTimeUtcTicks = parsedStartTimeUtcTicks;
            return true;
        }

        const string tokenPrefix = "token:";
        if (!generationSpan.StartsWith(tokenPrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(
                generationSpan[tokenPrefix.Length..],
                "N",
                out var parsedIncarnationToken))
        {
            return false;
        }

        incarnationToken = parsedIncarnationToken;
        return true;
    }

    private static long? TryGetCurrentProcessStartTimeUtcTicks()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return TryGetProcessStartTimeUtcTicks(process, out var startTimeUtcTicks)
                ? startTimeUtcTicks
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryGetProcessStartTimeUtcTicks(Process process, out long startTimeUtcTicks)
    {
        try
        {
            startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            return startTimeUtcTicks > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or NotSupportedException)
        {
            startTimeUtcTicks = 0;
            return false;
        }
    }

    public int GetFtsIncrementalWritesSinceOptimize()
    {
        var raw = GetMetaString(FtsIncrementalWritesSinceOptimizeMetaKey);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    public int GetFtsIncrementalWritesSinceMerge()
    {
        var raw = GetMetaString(FtsIncrementalWritesSinceMergeMetaKey);
        if (raw == null)
        {
            // Preserve the existing cadence when opening a database created before the
            // dedicated merge counter existed. / 専用 merge counter 導入前の DB でも
            // 既存 cadence を引き継ぐ。
            return GetFtsIncrementalWritesSinceOptimize();
        }

        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    public int RecordFtsIncrementalWrite()
    {
        var optimizeValue = Math.Min((long)GetFtsIncrementalWritesSinceOptimize() + 1, int.MaxValue);
        var mergeValue = Math.Min((long)GetFtsIncrementalWritesSinceMerge() + 1, int.MaxValue);
        SetMetaValues(
            (FtsIncrementalWritesSinceOptimizeMetaKey, optimizeValue.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (FtsIncrementalWritesSinceMergeMetaKey, mergeValue.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return (int)optimizeValue;
    }

    /// <summary>
    /// Record one incremental indexing run and run an FTS merge only when the write threshold is reached.
    /// incremental indexing runを1回記録し、write threshold到達時だけ FTS merge を行う。
    /// </summary>
    public bool RecordFtsIncrementalWriteAndMergeIfThresholdReached(
        Action? beforeMerge = null,
        int threshold = DefaultFtsMergeIncrementalWriteThreshold,
        int mergeWorkTargetPages = DefaultFtsIncrementalMergeWorkTargetPages,
        CancellationToken cancellationToken = default)
    {
        if (threshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        if (mergeWorkTargetPages <= 0)
            throw new ArgumentOutOfRangeException(nameof(mergeWorkTargetPages));

        RecordFtsIncrementalWrite();
        if (GetFtsIncrementalWritesSinceMerge() < threshold)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        beforeMerge?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        MergeFtsSegments(mergeWorkTargetPages, cancellationToken);
        return true;
    }

    /// <summary>
    /// Record one incremental indexing run and fully optimize only when the write threshold is reached.
    /// Existing callers keep the original full-optimize contract; index runners use the incremental merge API.
    /// incremental indexing runを1回記録し、write threshold到達時だけ完全 optimize する。
    /// 既存 caller の契約は維持し、index runner は incremental merge API を使用する。
    /// </summary>
    public bool RecordFtsIncrementalWriteAndOptimizeIfThresholdReached(
        Action? beforeOptimize = null,
        int threshold = DefaultFtsOptimizeIncrementalWriteThreshold,
        CancellationToken cancellationToken = default)
    {
        if (threshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));

        if (RecordFtsIncrementalWrite() < threshold)
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        beforeOptimize?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        OptimizeFts(cancellationToken);
        return true;
    }

    public void MarkBatchInProgress() => SetMeta(DbContext.BatchInProgressMetaKey, "true");

    public void ClearBatchInProgress() => SetMeta(DbContext.BatchInProgressMetaKey, "false");

    public bool OptimizeFtsIfIncrementalWriteThresholdReached(
        int threshold = DefaultFtsOptimizeIncrementalWriteThreshold,
        CancellationToken cancellationToken = default)
    {
        if (threshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(threshold));

        if (GetFtsIncrementalWritesSinceOptimize() < threshold)
            return false;

        OptimizeFts(cancellationToken);
        return true;
    }
}
