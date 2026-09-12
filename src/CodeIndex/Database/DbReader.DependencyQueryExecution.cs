using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    internal sealed record DependencyQueryResult(
        List<FileDependencyResult> Edges,
        bool CandidateScanComplete,
        bool ResultWindowComplete);

    private DependencyQueryResult ExecuteDependencyQuery(
        DependencyQueryPlan plan,
        CancellationToken cancellationToken)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = plan.Sql;
        BindDependencyQueryParameters(command, plan.Parameters);

        var results = new List<FileDependencyResult>();
        var sourceScanComplete = false;
        cancellationToken.ThrowIfCancellationRequested();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((SqliteCommand)state!).Cancel(),
            command);
        try
        {
            using var reader = command.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (plan.Request.CaptureSummaryCoverage)
                {
                    sourceScanComplete = reader.GetBoolean(5);
                    // The coverage row survives an empty edge result.
                    if (reader.IsDBNull(0))
                        continue;
                }
                results.Add(ProjectDependencyRow(reader));
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        // Reuse the existing bounded ranking window. Looking ahead must not
        // enlarge either the SQL candidate budget or the C# source budget.
        var outputLimit = plan.Request.CaptureSummaryCoverage && plan.Request.Limit < int.MaxValue
            ? plan.Request.Limit + 1
            : plan.Request.Limit;
        var candidateScanComplete = sourceScanComplete
            && results.Count < DependencyNoiseProfile.GetRankingCandidateLimit(plan.Request.Limit);
        var resultWindowComplete = results.Count <= outputLimit;
        return new(
            RankDependencyResults(results, outputLimit, plan.Request.SuppressDependencyNoise),
            candidateScanComplete,
            resultWindowComplete);
    }
}
