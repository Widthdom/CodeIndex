namespace CodeIndex.Database;

internal sealed record AuditScopeIndexedFileCoverageSnapshot(
    int IncludedCount,
    List<string> IncludedPaths,
    int ExcludedCount,
    List<string> ExcludedPaths);

internal sealed record AuditScopeUnknownFileInventorySnapshot(
    bool Available,
    long? TotalCount,
    IReadOnlyList<string> Paths,
    bool PathsTruncated);

internal sealed record AuditScopeKnownUnindexedFileCoverageSnapshot(
    bool Available,
    int Count,
    List<string> Paths);

public partial class DbReader
{
    internal AuditScopeIndexedFileCoverageSnapshot GetAuditScopeIndexedFileCoverage(
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        DateTime? since,
        int pathLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pathLimit);
        Cancellation.ThrowIfCancellationRequested();

        var scopedSql = BuildAuditScopeEligibleFileSql(
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            since);
        var includedCount = ReadAuditScopeFileCount(
            scopedSql,
            lang,
            pathPatterns,
            excludePathPatterns,
            since);
        var includedPaths = ReadAuditScopeIncludedPathSample(
            scopedSql,
            lang,
            pathPatterns,
            excludePathPatterns,
            since,
            pathLimit);
        var indexedCount = ReadAuditScopeTotalIndexedFileCount();
        var excludedCount = Math.Max(0, indexedCount - includedCount);
        var excludedPaths = ReadAuditScopeExcludedPathSample(
            scopedSql,
            lang,
            pathPatterns,
            excludePathPatterns,
            since,
            pathLimit);
        Cancellation.ThrowIfCancellationRequested();
        return new(includedCount, includedPaths, excludedCount, excludedPaths);
    }

    internal AuditScopeKnownUnindexedFileCoverageSnapshot GetAuditScopeKnownUnindexedFileCoverage(
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        DateTime? since,
        int pathLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pathLimit);
        Cancellation.ThrowIfCancellationRequested();
        if (!_hasIssuesPhysicalTable)
            return new(false, 0, []);

        var scopedSql = BuildAuditScopeFilteredFileSql(
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            since)
            + " AND EXISTS (SELECT 1 FROM file_issues coverage_issue"
            + " WHERE coverage_issue.file_id = f.id AND coverage_issue.kind = 'file_too_large')";
        var count = ReadAuditScopeFileCount(
            scopedSql,
            lang,
            pathPatterns,
            excludePathPatterns,
            since);
        var paths = ReadAuditScopeIncludedPathSample(
            scopedSql,
            lang,
            pathPatterns,
            excludePathPatterns,
            since,
            pathLimit);
        Cancellation.ThrowIfCancellationRequested();
        return new(true, count, paths);
    }

    private string BuildAuditScopeEligibleFileSql(
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        DateTime? since)
    {
        var sql = BuildAuditScopeFilteredFileSql(
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            since);
        if (_hasIssuesPhysicalTable)
        {
            sql += " AND NOT EXISTS (SELECT 1 FROM file_issues coverage_issue"
                + " WHERE coverage_issue.file_id = f.id AND coverage_issue.kind = 'file_too_large')";
        }
        return sql;
    }

    private string BuildAuditScopeFilteredFileSql(
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        DateTime? since)
    {
        lang = NormalizeQueryLanguage(lang);
        var sql = "SELECT f.id FROM files f WHERE 1=1";
        if (lang != null)
            sql += " AND f.lang = @coverageLang";
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @coverageSince";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        return sql;
    }

    private int ReadAuditScopeFileCount(
        string scopedSql,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        DateTime? since)
    {
        using var command = _conn.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM ({scopedSql})";
        AddAuditScopeFilterParameters(command, lang, pathPatterns, excludePathPatterns, since);
        return checked((int)Convert.ToInt64(command.ExecuteScalar()));
    }

    private List<string> ReadAuditScopeIncludedPathSample(
        string scopedSql,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        DateTime? since,
        int pathLimit)
    {
        if (pathLimit == 0)
            return [];

        using var command = _conn.CreateCommand();
        command.CommandText = $"WITH scoped AS ({scopedSql})"
            + " SELECT f.path FROM files f JOIN scoped ON scoped.id = f.id"
            + " ORDER BY f.path LIMIT @coverageLimit";
        AddAuditScopeFilterParameters(command, lang, pathPatterns, excludePathPatterns, since);
        SqliteCommandPolicy.Add(command, "@coverageLimit", pathLimit);
        return ReadAuditScopePaths(command, pathLimit);
    }

    private int ReadAuditScopeTotalIndexedFileCount()
    {
        using var command = _conn.CreateCommand();
        command.CommandText = _hasIssuesPhysicalTable
            ? "SELECT COUNT(*) FROM files f WHERE NOT EXISTS (SELECT 1 FROM file_issues coverage_issue"
                + " WHERE coverage_issue.file_id = f.id AND coverage_issue.kind = 'file_too_large')"
            : "SELECT COUNT(*) FROM files";
        return checked((int)Convert.ToInt64(command.ExecuteScalar()));
    }

    internal AuditScopeUnknownFileInventorySnapshot GetAuditScopeUnknownFileInventory()
    {
        var diagnosticsCurrent =
            ParseMetaLong(TryGetMetaStringInternal(DbContext.UnknownExtensionDiagnosticsVersionMetaKey))
            == DbContext.UnknownExtensionDiagnosticsVersion;
        if (!diagnosticsCurrent)
            return new(false, null, [], false);

        var totalCount = ParseMetaLong(TryGetMetaStringInternal(DbContext.UnknownExtensionFileCountMetaKey));
        var paths = ParseMetaStringList(TryGetMetaStringInternal(DbContext.UnknownExtensionFilePathsMetaKey)) ?? [];
        if (!totalCount.HasValue || totalCount.Value < paths.Count)
            return new(false, null, [], false);

        var truncated = ParseMetaBool(TryGetMetaStringInternal(DbContext.UnknownExtensionFilesTruncatedMetaKey)) == true
            || totalCount.Value > paths.Count;
        return new(
            true,
            totalCount,
            paths,
            truncated);
    }

    internal bool AuditScopePathMatches(
        string path,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        using var command = _conn.CreateCommand();
        var sql = "SELECT 1 FROM (SELECT @coveragePath AS path) f WHERE 1=1";
        AppendPathFilters(
            ref sql,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            applyGeneratedFilter: false);
        sql += " LIMIT 1";
        command.CommandText = sql;
        SqliteCommandPolicy.AddText(command, "@coveragePath", path);
        AddPathFilterParameters(command, pathPatterns, excludePathPatterns);
        return command.ExecuteScalar() != null;
    }

    private List<string> ReadAuditScopeExcludedPathSample(
        string scopedSql,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        DateTime? since,
        int pathLimit)
    {
        if (pathLimit == 0)
            return [];

        var sql = $"WITH scoped AS ({scopedSql}) SELECT f.path FROM files f"
            + " WHERE NOT EXISTS (SELECT 1 FROM scoped WHERE scoped.id = f.id)";
        if (_hasIssuesPhysicalTable)
        {
            sql += " AND NOT EXISTS (SELECT 1 FROM file_issues coverage_issue"
                + " WHERE coverage_issue.file_id = f.id AND coverage_issue.kind = 'file_too_large')";
        }
        sql += " ORDER BY f.path LIMIT @coverageLimit";

        using var command = _conn.CreateCommand();
        command.CommandText = sql;
        AddAuditScopeFilterParameters(command, lang, pathPatterns, excludePathPatterns, since);
        SqliteCommandPolicy.Add(command, "@coverageLimit", pathLimit);

        return ReadAuditScopePaths(command, pathLimit);
    }

    private void AddAuditScopeFilterParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        DateTime? since)
    {
        lang = NormalizeQueryLanguage(lang);
        if (lang != null)
            SqliteCommandPolicy.AddText(command, "@coverageLang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            SqliteCommandPolicy.Add(command, "@coverageSince", since.Value);
        AddPathFilterParameters(command, pathPatterns, excludePathPatterns);
    }

    private List<string> ReadAuditScopePaths(Microsoft.Data.Sqlite.SqliteCommand command, int pathLimit)
    {
        var paths = new List<string>(Math.Min(pathLimit, 64));
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            Cancellation.ThrowIfCancellationRequested();
            paths.Add(reader.GetString(0));
        }
        return paths;
    }
}
