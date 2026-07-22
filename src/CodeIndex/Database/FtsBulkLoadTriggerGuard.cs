namespace CodeIndex.Database;

internal sealed class FtsBulkLoadTriggerGuard : IDisposable
{
    internal const int DirtyByteThresholdNumerator = 3;
    internal const int DirtyByteThresholdDenominator = 5;

    private readonly Func<bool>? _shouldRebuildOnAbandon;
    private readonly long _durableCommitGenerationAtStart;
    private DbWriter? _writer;

    private FtsBulkLoadTriggerGuard(DbWriter writer, Func<bool>? shouldRebuildOnAbandon)
    {
        _writer = writer;
        _shouldRebuildOnAbandon = shouldRebuildOnAbandon;
        writer.SuspendFtsSyncTriggersForBulkLoad();
        _durableCommitGenerationAtStart = writer.CaptureDurableCommitGeneration();
    }

    public static FtsBulkLoadTriggerGuard? Start(
        DbWriter writer,
        bool enabled,
        Func<bool>? shouldRebuildOnAbandon = null)
        => enabled ? new FtsBulkLoadTriggerGuard(writer, shouldRebuildOnAbandon) : null;

    internal static bool ShouldUseForDirtyBytes(long dirtyBytes, long totalBytes)
    {
        if (dirtyBytes <= 0 || totalBytes <= 0)
            return false;
        if (dirtyBytes >= totalBytes)
            return true;

        // Compare against ceil(totalBytes * 3 / 5) without overflowing long.
        // long overflow を避けて ceil(totalBytes * 3 / 5) と比較する。
        var quotient = totalBytes / DirtyByteThresholdDenominator;
        var remainder = totalBytes % DirtyByteThresholdDenominator;
        var threshold = quotient * DirtyByteThresholdNumerator
            + (remainder * DirtyByteThresholdNumerator + DirtyByteThresholdDenominator - 1)
                / DirtyByteThresholdDenominator;
        return dirtyBytes >= threshold;
    }

    internal static bool TryUpdateKnownByteTotal(
        long totalBytes,
        long? previousBytes,
        long currentBytes,
        out long updatedTotalBytes)
    {
        updatedTotalBytes = long.MaxValue;
        if (totalBytes < 0
            || currentBytes < 0
            || previousBytes is < 0
            || previousBytes > totalBytes)
        {
            return false;
        }

        var totalWithoutPrevious = totalBytes - previousBytes.GetValueOrDefault();
        if (totalWithoutPrevious > long.MaxValue - currentBytes)
            return false;

        updatedTotalBytes = totalWithoutPrevious + currentBytes;
        return true;
    }

    internal static bool TryAccumulateDirtyFileBytes(
        long dirtyBytes,
        long persistedSizeExcessBytes,
        long currentSize,
        PersistedIndexedFileSize persistedSize,
        out long updatedDirtyBytes,
        out long updatedPersistedSizeExcessBytes)
    {
        updatedDirtyBytes = long.MaxValue;
        updatedPersistedSizeExcessBytes = long.MaxValue;
        if (dirtyBytes < 0
            || persistedSizeExcessBytes < 0
            || currentSize < 0
            || (persistedSize.Exists && (!persistedSize.SizeKnown || persistedSize.Size < 0)))
        {
            return false;
        }

        var oldSize = persistedSize.Exists ? persistedSize.Size : currentSize;
        var dirtyContribution = Math.Max(oldSize, currentSize);
        var persistedSizeExcess = oldSize > currentSize ? oldSize - currentSize : 0;
        if (dirtyBytes > long.MaxValue - dirtyContribution
            || persistedSizeExcessBytes > long.MaxValue - persistedSizeExcess)
        {
            return false;
        }

        updatedDirtyBytes = dirtyBytes + dirtyContribution;
        updatedPersistedSizeExcessBytes = persistedSizeExcessBytes + persistedSizeExcess;
        return true;
    }

    public void Complete(
        bool rebuild,
        Action? beforeOptimize = null,
        CancellationToken cancellationToken = default)
    {
        var writer = _writer;
        if (writer == null)
            return;

        try
        {
            writer.RestoreFtsSyncTriggers(cancellationToken);
            rebuild |= writer.HasDurableCommitSince(_durableCommitGenerationAtStart);
            if (rebuild)
                writer.RebuildFtsFromChunks(
                    resetIncrementalWriteCounter: false,
                    cancellationToken: cancellationToken);

            if (rebuild)
            {
                beforeOptimize?.Invoke();
                writer.OptimizeFts(cancellationToken);
            }

            writer.ClearFtsBulkLoadInProgress();
            _writer = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Do not synchronously repeat a potentially long rebuild while unwinding a canceled
            // watch sub-run. Restore triggers and leave a stale marker for the next run.
            // cancel 済み watch sub-run の unwind 中に長い rebuild を繰り返さない。trigger を
            // 復元し、次回 run 用の stale marker を残す。
            writer.RestoreFtsSyncTriggers();
            writer.MarkFtsBulkLoadRecoveryNeeded();
            _writer = null;
            throw;
        }
    }

    public void Dispose()
    {
        var writer = _writer;
        if (writer == null)
            return;

        try
        {
            // Restore triggers even on interrupted bulk loads. If any rows committed while
            // triggers were disabled, callers can request a rebuild so committed progress
            // stays searchable even when the normal Complete path is abandoned.
            // bulk load 中断時も trigger を復元する。trigger 無効中に commit 済み行があれば
            // FTS を再構築し、Complete まで進まなかった commit 済み progress も検索可能に保つ。
            var hasAbandonPolicy = _shouldRebuildOnAbandon != null;
            var rebuild = _shouldRebuildOnAbandon?.Invoke() == true
                || writer.HasDurableCommitSince(_durableCommitGenerationAtStart);
            writer.RestoreFtsSyncTriggers();
            if (rebuild)
                writer.RebuildFtsFromChunks();
            if (hasAbandonPolicy)
                writer.ClearFtsBulkLoadInProgress();
            else
                writer.MarkFtsBulkLoadRecoveryNeeded();
        }
        catch
        {
            // Cleanup can fail after triggers were dropped or committed chunks diverged from
            // FTS. Replace the process-owned marker best-effort so a later request in this same
            // process does not mistake the interrupted owner for an active bulk load. Never mask
            // the original restore/rebuild exception if even the marker write also fails.
            // trigger drop 後または committed chunk と FTS の不一致中に cleanup が失敗しても、
            // 同一 process の次回 request が owner を active と誤認しないよう marker を
            // best-effort で owner 非依存へ置き換える。marker write 失敗で元例外を隠さない。
            try
            {
                writer.MarkFtsBulkLoadRecoveryNeeded();
            }
            catch
            {
                // The original cleanup failure is the actionable error.
            }
            throw;
        }
        finally
        {
            _writer = null;
        }
    }
}
