using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

/// <summary>
/// Temporarily removes reference-query and graph indexes while raw rows are populated, then
/// removes the candidate reverse lookup only when graph candidate materialization begins.
/// File and reference-line maintenance indexes remain available throughout the load.
/// </summary>
internal sealed class ReferenceSecondaryIndexBulkLoadGuard : IDisposable
{
    private readonly bool _restoreOnDispose;
    private readonly bool _refreshPlannerStatisticsBeforeCandidatePopulation;
    private DbWriter? _writer;
    private bool _plannerStatisticsRefreshAttempted;

    private ReferenceSecondaryIndexBulkLoadGuard(
        DbWriter writer,
        bool restoreOnDispose,
        bool refreshPlannerStatisticsBeforeCandidatePopulation,
        CancellationToken cancellationToken)
    {
        _restoreOnDispose = restoreOnDispose;
        _refreshPlannerStatisticsBeforeCandidatePopulation =
            refreshPlannerStatisticsBeforeCandidatePopulation;
        try
        {
            // Scoped graph planning names deferred indexes explicitly. Force the active
            // scope onto its full-refresh plan before any of those indexes disappear.
            writer.RequireFullReferenceGraphRefreshForSecondaryIndexDeferral();
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
    /// Canonical index names deferred and restored by this guard. Exposed internally so
    /// integration tests can verify the exact schema contract without duplicating the DDL.
    /// </summary>
    internal static IReadOnlyList<string> IndexNames { get; }
        = Array.AsReadOnly(
            ReferenceSecondaryIndexSql.DeferredDuringBulkLoad
                .Select(static definition => definition.Name)
                .ToArray());

    internal static IReadOnlyList<string> GraphFinalizationIndexNames { get; }
        = Array.AsReadOnly(
            ReferenceSecondaryIndexSql.GraphFinalizationRequired
                .Select(static definition => definition.Name)
                .ToArray());

    internal static IReadOnlyList<string> DeferredGraphPreparationIndexNames { get; }
        = Array.AsReadOnly(
            ReferenceSecondaryIndexSql.DeferredGraphPreparation
                .Select(static definition => definition.Name)
                .ToArray());

    internal static IReadOnlyList<string> CandidatePopulationIndexNames { get; }
        = Array.AsReadOnly(
            ReferenceSecondaryIndexSql.CandidatePopulationDeferred
                .Select(static definition => definition.Name)
                .ToArray());

    internal static ReferenceSecondaryIndexBulkLoadGuard? StartTransactional(
        DbWriter writer,
        bool enabled,
        CancellationToken cancellationToken = default,
        bool refreshPlannerStatisticsBeforeCandidatePopulation = false)
    {
        if (!enabled)
            return null;

        writer.RequireCallerOwnedTransactionForReferenceSecondaryIndexBulkLoad();
        return new ReferenceSecondaryIndexBulkLoadGuard(
            writer,
            restoreOnDispose: false,
            refreshPlannerStatisticsBeforeCandidatePopulation,
            cancellationToken);
    }

    internal static ReferenceSecondaryIndexBulkLoadGuard? StartRecoverable(
        DbWriter writer,
        bool enabled,
        CancellationToken cancellationToken = default,
        bool refreshPlannerStatisticsBeforeCandidatePopulation = false)
        => enabled
            ? new ReferenceSecondaryIndexBulkLoadGuard(
                writer,
                restoreOnDispose: true,
                refreshPlannerStatisticsBeforeCandidatePopulation,
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

    internal void ReportIdentityRefreshStarted()
        => _writer?.ReportReferenceSecondaryIndexBulkLoadState("identity_started");

    /// <summary>
    /// Drop the candidate reverse lookup only once a graph refresh will actually rebuild
    /// candidate rows. Raw reference persistence and marker-only readiness work never touch
    /// that table, so keeping the index until this boundary avoids a full no-op rebuild.
    /// graph更新がcandidate rowを実際に再構築する直前だけ逆引きindexをdropする。
    /// </summary>
    internal void PrepareForCandidatePopulation(CancellationToken cancellationToken = default)
    {
        var writer = _writer;
        if (writer == null)
            return;

        writer.DropCandidatePopulationReferenceSecondaryIndexes(cancellationToken);
        if (!_refreshPlannerStatisticsBeforeCandidatePopulation
            || _plannerStatisticsRefreshAttempted)
            return;

        _plannerStatisticsRefreshAttempted = true;
        writer.RefreshFreshBulkLoadPlannerStatistics(cancellationToken);
    }

    internal void PrepareForMutualRecursion(CancellationToken cancellationToken = default)
        => _writer?.RestoreGraphFinalizationRequiredReferenceSecondaryIndexes(cancellationToken);

    /// <summary>
    /// Restore every deferred index except the candidate reverse lookup. A later graph
    /// refresh can then populate candidates without maintaining that B-tree, while readiness
    /// work retains the ordinary reference query paths.
    /// candidate逆引き以外を復元し、readiness queryを保ったまま後段graph構築を遅延する。
    /// </summary>
    internal void PrepareForDeferredGraphRefresh(CancellationToken cancellationToken = default)
        => _writer?.RestoreReferenceSecondaryIndexesForDeferredGraph(cancellationToken);

    internal void ReportMutualRecursionStarted()
        => _writer?.ReportReferenceSecondaryIndexBulkLoadState("mutual_started");

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
    private const string RefreshFreshBulkLoadPlannerStatisticsSql = """
        ANALYZE main.files;
        ANALYZE main.symbols;
        ANALYZE main.symbol_references;
        """;

    internal void DropDeferredReferenceSecondaryIndexes(CancellationToken cancellationToken)
    {
        // Old binaries may have recreated retired indexes after a database was pruned by a
        // newer version. Drop them as part of setup, but never restore them after the load.
        foreach (var indexName in ReferenceSecondaryIndexSql.Retired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute($"DROP INDEX IF EXISTS {indexName}", cancellationToken);
        }

        // Candidate rows are not touched by raw reference persistence. Keep their reverse
        // lookup until a graph refresh actually reaches candidate materialization so a
        // high-cardinality no-op update never pays to rebuild the whole candidate index.
        foreach (var definition in ReferenceSecondaryIndexSql.DeferredGraphPreparation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute($"DROP INDEX IF EXISTS {definition.Name}", cancellationToken);
        }

        ReferenceSecondaryIndexBulkLoadStateForTesting?.Invoke(_conn, "dropped");
    }

    internal void DropCandidatePopulationReferenceSecondaryIndexes(
        CancellationToken cancellationToken = default)
    {
        foreach (var definition in ReferenceSecondaryIndexSql.CandidatePopulationDeferred)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute($"DROP INDEX IF EXISTS {definition.Name}", cancellationToken);
        }

        ReportReferenceSecondaryIndexBulkLoadState("candidate_deferred");
    }

    internal void RestoreDeferredReferenceSecondaryIndexes(
        CancellationToken cancellationToken = default)
    {
        // Re-run the graph subset defensively. CREATE IF NOT EXISTS is cheap after a
        // successful graph transaction and repairs it if a caller abandoned that phase.
        RestoreReferenceSecondaryIndexes(
            ReferenceSecondaryIndexSql.GraphFinalizationRequired,
            cancellationToken);
        RestoreReferenceSecondaryIndexes(
            ReferenceSecondaryIndexSql.RemainingQuery,
            cancellationToken);

        ReportReferenceSecondaryIndexBulkLoadState("restored");
    }

    internal void RestoreGraphFinalizationRequiredReferenceSecondaryIndexes(
        CancellationToken cancellationToken = default)
    {
        RestoreReferenceSecondaryIndexes(
            ReferenceSecondaryIndexSql.GraphFinalizationRequired,
            cancellationToken);
        ReportReferenceSecondaryIndexBulkLoadState("graph_required_restored");
    }

    internal void RestoreReferenceSecondaryIndexesForDeferredGraph(
        CancellationToken cancellationToken = default)
    {
        RestoreReferenceSecondaryIndexes(
            ReferenceSecondaryIndexSql.DeferredGraphPreparation,
            cancellationToken);
        ReportReferenceSecondaryIndexBulkLoadState("deferred_graph_prepared");
    }

    private void RestoreReferenceSecondaryIndexes(
        IReadOnlyList<ReferenceSecondaryIndexDefinition> definitions,
        CancellationToken cancellationToken)
    {
        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute(definition.CreateSql, cancellationToken);
        }
    }

    internal void ReportReferenceSecondaryIndexBulkLoadState(string phase)
        => ReferenceSecondaryIndexBulkLoadStateForTesting?.Invoke(_conn, phase);

    internal void RefreshFreshBulkLoadPlannerStatistics(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var transaction = BeginTransaction(
                cancellationToken,
                "refresh fresh bulk-load planner statistics");
            try
            {
                FreshBulkLoadPlannerStatisticsStateForTesting?.Invoke(
                    _conn,
                    "post_load_statistics_started");
                Execute(RefreshFreshBulkLoadPlannerStatisticsSql, cancellationToken);
                FreshBulkLoadPlannerStatisticsStateForTesting?.Invoke(
                    _conn,
                    "post_load_statistics_completed");
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        catch (SqliteException)
        {
            // Fresh statistics improve only this graph plan. If SQLite rejects ANALYZE,
            // keep the enclosing bulk load alive with the prior planner state.
            // fresh statisticsは今回のgraph planだけを改善するため、SQLiteがANALYZEを
            // 拒否した場合はsavepointを戻し、従来のplanner stateでbulk loadを続行する。
            FreshBulkLoadPlannerStatisticsStateForTesting?.Invoke(
                _conn,
                "post_load_statistics_failed");
        }
    }

    internal void RequireCallerOwnedTransactionForReferenceSecondaryIndexBulkLoad()
        => RequireCallerOwnedTransaction(nameof(ReferenceSecondaryIndexBulkLoadGuard));
}
