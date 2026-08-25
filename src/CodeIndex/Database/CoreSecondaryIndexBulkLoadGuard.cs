namespace CodeIndex.Database;

/// <summary>
/// Defers language-neutral secondary indexes while an authoritative empty-database
/// CLI transaction persists its first snapshot. Disposal deliberately leaves schema
/// recovery to the caller-owned transaction rollback; successful callers must Complete.
/// </summary>
internal sealed class CoreSecondaryIndexBulkLoadGuard : IDisposable
{
    private DbWriter? _writer;

    private CoreSecondaryIndexBulkLoadGuard(
        DbWriter writer,
        CancellationToken cancellationToken)
    {
        writer.DropCoreSecondaryIndexes(cancellationToken);
        _writer = writer;
    }

    internal static IReadOnlyList<string> IndexNames { get; }
        = Array.AsReadOnly(
            CoreSecondaryIndexSql.DeferredDuringAuthoritativeFreshLoad
                .Select(static definition => definition.Name)
                .ToArray());

    internal static CoreSecondaryIndexBulkLoadGuard? StartTransactional(
        DbWriter writer,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
            return null;

        writer.RequireCallerOwnedTransactionForCoreSecondaryIndexBulkLoad();
        return new CoreSecondaryIndexBulkLoadGuard(writer, cancellationToken);
    }

    internal void Complete(CancellationToken cancellationToken = default)
    {
        var writer = _writer;
        if (writer == null)
            return;

        writer.RequireCallerOwnedTransactionForCoreSecondaryIndexBulkLoad();
        writer.RestoreCoreSecondaryIndexes(cancellationToken);
        _writer = null;
    }

    public void Dispose()
    {
        // The production scope is nested in one atomic full-scan transaction. Rebuilding
        // deferred indexes while cancellation unwinds would waste work rollback removes.
        // production scopeはatomicなfull-scan transaction内にあり、cancel時は
        // rollbackがDDLを復元するためdeferred indexの再構築は行わない。
        _writer = null;
    }
}

public partial class DbWriter
{
    internal void DropCoreSecondaryIndexes(
        CancellationToken cancellationToken = default)
    {
        foreach (var definition in CoreSecondaryIndexSql.DeferredDuringAuthoritativeFreshLoad)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute($"DROP INDEX IF EXISTS {definition.Name}", cancellationToken);
        }

        CoreSecondaryIndexBulkLoadStateForTesting?.Invoke(_conn, "dropped");
    }

    internal void RestoreCoreSecondaryIndexes(
        CancellationToken cancellationToken = default)
    {
        foreach (var definition in CoreSecondaryIndexSql.DeferredDuringAuthoritativeFreshLoad)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute(definition.CreateSql, cancellationToken);
        }

        CoreSecondaryIndexBulkLoadStateForTesting?.Invoke(_conn, "restored");
    }

    internal void RequireCallerOwnedTransactionForCoreSecondaryIndexBulkLoad()
        => RequireCallerOwnedTransaction(nameof(CoreSecondaryIndexBulkLoadGuard));
}
