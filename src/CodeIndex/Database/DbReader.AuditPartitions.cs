namespace CodeIndex.Database;

public partial class DbReader
{
    private static readonly AsyncLocal<string?> AuditPartitionPath = new();

    internal static IDisposable BeginAuditPartitionPath(string? path)
        => new AuditPartitionPathLease(path);

    private sealed class AuditPartitionPathLease : IDisposable
    {
        private readonly string? _previous = AuditPartitionPath.Value;
        internal AuditPartitionPathLease(string? path) => AuditPartitionPath.Value = path;
        public void Dispose() => AuditPartitionPath.Value = _previous;
    }

    // The caller shares one row budget across all effective child scopes. No counts,
    // content reads, or unbounded inventory allocation are needed to build the plan.
    internal List<string> GetAuditPartitionPaths(string? lang, IReadOnlyList<string> paths,
        IReadOnlyList<string> excludes, bool excludeTests, DateTime? since,
        IReadOnlyList<string>? requiredPaths, int limit, int pathByteLimit)
    {
        var sql = BuildAuditScopeEligibleFileSql(lang, paths, excludes, excludeTests, since);
        AppendAdditionalPathIncludeFilters(ref sql, requiredPaths, "partitionRequired");
        using var command = _conn.CreateCommand();
        command.CommandText = $"SELECT CASE WHEN length(CAST(f.path AS BLOB)) <= @partitionPathBytes THEN f.path END FROM files f WHERE f.id IN ({sql}) ORDER BY f.path COLLATE BINARY LIMIT @partitionLimit";
        AddAuditScopeFilterParameters(command, lang, paths, excludes, since);
        AddPathIncludeFilterParameters(command, requiredPaths, "partitionRequired");
        SqliteCommandPolicy.Add(command, "@partitionLimit", limit);
        SqliteCommandPolicy.Add(command, "@partitionPathBytes", pathByteLimit);
        var pathsFound = new List<string>();
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            Cancellation.ThrowIfCancellationRequested();
            // A sentinel rejects the entire inventory before allocating oversized text.
            pathsFound.Add(reader.IsDBNull(0) ? "\0" : reader.GetString(0));
        }
        return pathsFound;
    }
}
