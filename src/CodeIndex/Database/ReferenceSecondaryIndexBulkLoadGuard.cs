namespace CodeIndex.Database;

/// <summary>
/// Temporarily removes reference-query and graph indexes while a fresh index is populated.
/// File and reference-line maintenance indexes remain available throughout the load.
/// </summary>
internal sealed class ReferenceSecondaryIndexBulkLoadGuard : IDisposable
{
    private readonly bool _restoreOnDispose;
    private DbWriter? _writer;

    private ReferenceSecondaryIndexBulkLoadGuard(
        DbWriter writer,
        bool restoreOnDispose,
        CancellationToken cancellationToken)
    {
        _restoreOnDispose = restoreOnDispose;
        try
        {
            writer.DropDeferredReferenceSecondaryIndexes(cancellationToken);
            _writer = writer;
        }
        catch (Exception dropFailure) when (restoreOnDispose)
        {
            // MCP persists files in independent transactions. If setup only dropped a prefix
            // of the index set, restore that prefix before surfacing the setup failure.
            try
            {
                writer.RestoreDeferredReferenceSecondaryIndexes();
            }
            catch (Exception restoreFailure)
            {
                throw new AggregateException(
                    "Reference secondary-index bulk-load setup and recovery both failed.",
                    dropFailure,
                    restoreFailure);
            }

            throw;
        }
    }

    /// <summary>
    /// Index names removed by this guard. Exposed internally so integration tests can verify
    /// the exact schema contract without duplicating the canonical DDL list.
    /// </summary>
    internal static IReadOnlyList<string> IndexNames { get; }
        = Array.AsReadOnly(
            ReferenceSecondaryIndexSql.DeferredDuringBulkLoad
                .Select(static definition => definition.Name)
                .ToArray());

    internal static ReferenceSecondaryIndexBulkLoadGuard? StartTransactional(
        DbWriter writer,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
            return null;

        writer.RequireCallerOwnedTransactionForReferenceSecondaryIndexBulkLoad();
        return new ReferenceSecondaryIndexBulkLoadGuard(
            writer,
            restoreOnDispose: false,
            cancellationToken);
    }

    internal static ReferenceSecondaryIndexBulkLoadGuard? StartRecoverable(
        DbWriter writer,
        bool enabled,
        CancellationToken cancellationToken = default)
        => enabled
            ? new ReferenceSecondaryIndexBulkLoadGuard(
                writer,
                restoreOnDispose: true,
                cancellationToken)
            : null;

    internal void Complete(CancellationToken cancellationToken = default)
    {
        var writer = _writer;
        if (writer == null)
            return;

        writer.RestoreDeferredReferenceSecondaryIndexes(cancellationToken);
        _writer = null;
    }

    public void Dispose()
    {
        var writer = _writer;
        if (writer == null)
            return;

        try
        {
            // The transactional CLI mode deliberately relies on the caller's rollback when
            // Complete was abandoned. Rebuilding the indexes while unwinding cancellation
            // would add substantial work and the surrounding rollback restores the DDL.
            if (_restoreOnDispose)
                writer.RestoreDeferredReferenceSecondaryIndexes();
        }
        finally
        {
            // A restore failure remains visible to the caller; never turn a schema recovery
            // failure into a best-effort cleanup success.
            _writer = null;
        }
    }
}

public partial class DbWriter
{
    internal void DropDeferredReferenceSecondaryIndexes(CancellationToken cancellationToken)
    {
        foreach (var definition in ReferenceSecondaryIndexSql.DeferredDuringBulkLoad)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute($"DROP INDEX IF EXISTS {definition.Name}", cancellationToken);
        }

        ReferenceSecondaryIndexBulkLoadStateForTesting?.Invoke(_conn, "dropped");
    }

    internal void RestoreDeferredReferenceSecondaryIndexes(
        CancellationToken cancellationToken = default)
    {
        foreach (var definition in ReferenceSecondaryIndexSql.DeferredDuringBulkLoad)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute(definition.CreateSql, cancellationToken);
        }

        ReferenceSecondaryIndexBulkLoadStateForTesting?.Invoke(_conn, "restored");
    }

    internal void RequireCallerOwnedTransactionForReferenceSecondaryIndexBulkLoad()
        => RequireCallerOwnedTransaction(nameof(ReferenceSecondaryIndexBulkLoadGuard));
}
