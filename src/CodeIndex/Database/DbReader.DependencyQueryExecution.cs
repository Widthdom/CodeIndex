using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private List<FileDependencyResult> ExecuteDependencyQuery(
        DependencyQueryPlan plan,
        CancellationToken cancellationToken)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = plan.Sql;
        BindDependencyQueryParameters(command, plan.Parameters);

        var results = new List<FileDependencyResult>();
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
                results.Add(ProjectDependencyRow(reader));
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return RankDependencyResults(
            results,
            plan.Request.Limit,
            plan.Request.SuppressDependencyNoise);
    }
}
