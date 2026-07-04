namespace CodeIndex.Database;

internal sealed class FtsBulkLoadTriggerGuard : IDisposable
{
    private readonly Func<bool>? _shouldRebuildOnAbandon;
    private DbWriter? _writer;

    private FtsBulkLoadTriggerGuard(DbWriter writer, Func<bool>? shouldRebuildOnAbandon)
    {
        _writer = writer;
        _shouldRebuildOnAbandon = shouldRebuildOnAbandon;
        writer.SuspendFtsSyncTriggersForBulkLoad();
    }

    public static FtsBulkLoadTriggerGuard? Start(
        DbWriter writer,
        bool enabled,
        Func<bool>? shouldRebuildOnAbandon = null)
        => enabled ? new FtsBulkLoadTriggerGuard(writer, shouldRebuildOnAbandon) : null;

    public void Complete(bool rebuild, Action? beforeOptimize = null)
    {
        var writer = _writer;
        if (writer == null)
            return;

        try
        {
            writer.RestoreFtsSyncTriggers();
            if (rebuild)
                writer.RebuildFtsFromChunks();

            if (rebuild)
            {
                beforeOptimize?.Invoke();
                writer.OptimizeFts();
            }

            writer.ClearFtsBulkLoadInProgress();
        }
        finally
        {
            _writer = null;
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
            var rebuild = _shouldRebuildOnAbandon?.Invoke() == true;
            writer.RestoreFtsSyncTriggers();
            if (rebuild)
                writer.RebuildFtsFromChunks();
            if (hasAbandonPolicy)
                writer.ClearFtsBulkLoadInProgress();
        }
        finally
        {
            _writer = null;
        }
    }
}
