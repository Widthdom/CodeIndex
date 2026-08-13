using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private List<FileDependencyResult> ExecuteDependencyCycleQuery(
        DependencyCycleQueryPlan plan,
        CancellationToken cancellationToken,
        out int candidateRowCount)
    {
        candidateRowCount = 0;
        using var command = _conn.CreateCommand();
        command.CommandText = plan.Sql;
        BindDependencyQueryParameters(command, plan.Parameters);

        var results = new List<FileDependencyResult>();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((SqliteCommand)state!).Cancel(),
            command);
        try
        {
            using var reader = command.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidateRowCount++;
                var result = ProjectDependencyRow(reader);
                result.RankingScore = result.ReferenceCount;
                results.Add(result);
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return results;
    }
}
