using System.Diagnostics;

namespace CodeIndex.Database;

public partial class DbWriter
{
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
        SetMeta(FtsBulkLoadInProgressMetaKey, CreateFtsBulkLoadMarker());
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
        => SetMeta(FtsBulkLoadInProgressMetaKey, null);

    internal void MarkFtsBulkLoadRecoveryNeeded()
        => SetMeta(FtsBulkLoadInProgressMetaKey, "true");

    public bool RecoverInterruptedFtsBulkLoadIfNeeded(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var marker = GetMetaString(FtsBulkLoadInProgressMetaKey);
        if (!IsFtsBulkLoadMarkerSet(marker) || IsFtsBulkLoadOwnerActive(marker!))
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

    private static bool IsFtsBulkLoadMarkerSet(string? marker)
        => string.Equals(marker, "true", StringComparison.OrdinalIgnoreCase)
           || TryGetFtsBulkLoadOwnerPid(marker, out _);

    private static bool IsFtsBulkLoadOwnerActive(string marker)
    {
        if (!TryGetFtsBulkLoadOwnerPid(marker, out var pid))
            return false;

        if (pid == Environment.ProcessId)
            return true;

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
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
