using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private List<TResult> ExecuteGraphReferenceList<TResult>(
        GraphReferenceQueryPlan plan,
        Func<GraphReferenceRow, TResult> projector)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = plan.Sql;
        BindGraphReferenceQueryPlan(command, plan);

        var results = new List<TResult>();
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
            results.Add(projector(ReadGraphReferenceRow(reader, plan.Direction.RowLayout)));
        return results;
    }

    private int ExecuteGraphReferenceLimitedCount(GraphReferenceQueryPlan plan)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = plan.Sql;
        BindGraphReferenceQueryPlan(command, plan);
        var raw = command.ExecuteScalar();
        return raw is long count ? (int)count : Convert.ToInt32(raw);
    }

    private QueryCountResult ExecuteGraphReferenceTotalCount(GraphReferenceQueryPlan plan)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = plan.Sql;
        BindGraphReferenceQueryPlan(command, plan);
        return ExecuteCountSummary(command);
    }

    private IReadOnlyList<long> ExecuteGraphReferenceIdentityCandidates(
        GraphReferenceQueryPlan plan)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = plan.Sql;
        BindGraphReferenceQueryPlan(command, plan);

        var symbolIds = new List<long>();
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
            symbolIds.Add(reader.GetInt64(0));
        return symbolIds;
    }

    private GraphReferenceRow ReadGraphReferenceRow(
        SqliteDataReader reader,
        GraphReferenceRowLayout layout)
    {
        var primaryKind = reader.GetString(5);
        var kindAggregate = TruncateReferenceKindAggregate(
            GetNullableString(reader, layout.ReferenceKindsOrdinal),
            out var kindsTruncated);
        var countAggregate = TruncateReferenceKindAggregate(
            GetNullableString(reader, layout.ReferenceKindCountsOrdinal),
            out var countsTruncated);
        var referenceCount = reader.GetInt32(layout.ReferenceCountOrdinal);
        var firstColumn = layout.FirstColumnIsNullable
            ? GetNullableInt32(reader, layout.FirstColumnOrdinal)
            : reader.GetInt32(layout.FirstColumnOrdinal);
        return new GraphReferenceRow(
            reader.GetString(0),
            GetNullableString(reader, 1),
            GetNullableString(reader, 2),
            GetNullableString(reader, 3),
            reader.GetString(4),
            primaryKind,
            ParseDistinctReferenceKinds(kindAggregate, primaryKind),
            ParseReferenceKindCounts(countAggregate, primaryKind, referenceCount),
            kindsTruncated || countsTruncated,
            reader.GetDouble(layout.ReferenceWeightOrdinal),
            reader.GetInt32(layout.FirstLineOrdinal),
            firstColumn,
            layout.FirstLengthOrdinal is int lengthOrdinal ? GetNullableInt32(reader, lengthOrdinal) : null,
            referenceCount,
            layout.SelfReferenceOrdinal is int selfOrdinal && reader.GetInt32(selfOrdinal) != 0,
            layout.MutualRecursionOrdinal is int mutualOrdinal && reader.GetInt32(mutualOrdinal) != 0);
    }

    private static CallerResult ProjectCallerResult(GraphReferenceRow row)
        => new()
        {
            Path = row.Path,
            Lang = row.Lang,
            CallerKind = row.CallerKind,
            CallerName = row.CallerName,
            CalleeName = row.CalleeName,
            ReferenceKind = row.ReferenceKind,
            ReferenceKinds = row.ReferenceKinds,
            HasMixedReferenceKinds = row.ReferenceKinds.Count > 1,
            ReferenceKindCounts = row.ReferenceKindCounts,
            AggregateTruncated = row.AggregateTruncated,
            ReferenceWeightScore = row.ReferenceWeightScore,
            FirstLine = row.FirstLine,
            FirstColumn = row.FirstColumn ?? 0,
            FirstLength = row.FirstLength,
            ReferenceCount = row.ReferenceCount,
            HasSelfReference = row.HasSelfReference,
            HasMutualRecursion = row.HasMutualRecursion,
        };

    private static CalleeResult ProjectCalleeResult(GraphReferenceRow row)
        => new()
        {
            Path = row.Path,
            Lang = row.Lang,
            CallerKind = row.CallerKind,
            CallerName = row.CallerName,
            CalleeName = row.CalleeName,
            ReferenceKind = row.ReferenceKind,
            ReferenceKinds = row.ReferenceKinds,
            HasMixedReferenceKinds = row.ReferenceKinds.Count > 1,
            ReferenceKindCounts = row.ReferenceKindCounts,
            AggregateTruncated = row.AggregateTruncated,
            ReferenceWeightScore = row.ReferenceWeightScore,
            FirstLine = row.FirstLine,
            FirstColumn = row.FirstColumn,
            FirstLength = row.FirstLength,
            ReferenceCount = row.ReferenceCount,
        };

    private static IReadOnlyDictionary<string, int> ParseReferenceKindCounts(
        string? aggregate,
        string primaryKind,
        int fallbackCount)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["call"] = 0,
            ["instantiate"] = 0,
            ["subscribe"] = 0,
        };
        if (!string.IsNullOrWhiteSpace(aggregate))
        {
            foreach (var entry in aggregate.Split(','))
            {
                var separator = entry.LastIndexOf(':');
                if (separator <= 0 || separator == entry.Length - 1)
                    continue;
                var kind = entry[..separator].Trim();
                if (kind.Length == 0 || !int.TryParse(entry[(separator + 1)..], out var count))
                    continue;
                counts[kind] = counts.TryGetValue(kind, out var existing)
                    ? existing + count
                    : count;
            }
        }
        if (counts.Count == 0 && !string.IsNullOrEmpty(primaryKind))
            counts[primaryKind] = fallbackCount;
        return counts;
    }
}
