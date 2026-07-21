using System.Diagnostics;

namespace CodeIndex.Database;

public partial class DbWriter
{
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
            (FtsLastOptimizedAtMetaKey, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            (FtsLastOptimizeDurationMsMetaKey, stopwatch.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
        Execute(DbContext.DropFtsChunksSyncTriggersSql);
        _markWriteWork?.Invoke();
    }

    /// <summary>
    /// Restore FTS synchronization triggers after a bulk load.
    /// bulk load 後に FTS 同期 trigger を復元する。
    /// </summary>
    public void RestoreFtsSyncTriggers(CancellationToken cancellationToken = default)
    {
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
        Execute("INSERT INTO fts_chunks(fts_chunks) VALUES('rebuild')", cancellationToken);
        if (resetIncrementalWriteCounter)
            SetMeta(FtsIncrementalWritesSinceOptimizeMetaKey, "0");
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

    public int RecordFtsIncrementalWrite()
    {
        var value = GetFtsIncrementalWritesSinceOptimize() + 1;
        SetMeta(FtsIncrementalWritesSinceOptimizeMetaKey, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return value;
    }

    /// <summary>
    /// Record one incremental indexing run and optimize only when the write threshold is reached.
    /// incremental indexing runを1回記録し、write threshold到達時だけ最適化する。
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
