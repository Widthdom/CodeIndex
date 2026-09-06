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
        _cancellation.ThrowIfCancellationRequested();

        var includedCount = CountListFiles(
            lang: lang,
            pathPatterns: pathPatterns,
            excludePathPatterns: excludePathPatterns,
            excludeTests: excludeTests,
            since: since).FileCount;
        var includedPaths = ListFiles(
                limit: pathLimit,
                lang: lang,
                pathPatterns: pathPatterns,
                excludePathPatterns: excludePathPatterns,
                excludeTests: excludeTests,
                since: since)
            .Select(file => file.Path)
            .ToList();
        var indexedCount = ReadAuditScopeTotalIndexedFileCount();
        var excludedCount = Math.Max(0, indexedCount - includedCount);
        var excludedPaths = ReadAuditScopeExcludedPathSample(
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            since,
            pathLimit);
        _cancellation.ThrowIfCancellationRequested();
        return new(includedCount, includedPaths, excludedCount, excludedPaths);
    }

    private int ReadAuditScopeTotalIndexedFileCount()
    {
        using var command = _conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM files";
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
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        DateTime? since,
        int pathLimit)
    {
        if (pathLimit == 0)
            return [];

        lang = NormalizeQueryLanguage(lang);
        var scopedSql = "SELECT f.id FROM files f WHERE 1=1";
        if (lang != null)
            scopedSql += " AND f.lang = @coverageLang";
        if (since != null && _fileColumns.Contains("modified"))
            scopedSql += " AND f.modified >= @coverageSince";
        AppendPathFilters(ref scopedSql, pathPatterns, excludePathPatterns, excludeTests);

        var sql = $"WITH scoped AS ({scopedSql}) SELECT f.path FROM files f"
            + " WHERE NOT EXISTS (SELECT 1 FROM scoped WHERE scoped.id = f.id)"
            + " ORDER BY f.path LIMIT @coverageLimit";

        using var command = _conn.CreateCommand();
        command.CommandText = sql;
        if (lang != null)
            SqliteCommandPolicy.AddText(command, "@coverageLang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            SqliteCommandPolicy.Add(command, "@coverageSince", since.Value);
        AddPathFilterParameters(command, pathPatterns, excludePathPatterns);
        SqliteCommandPolicy.Add(command, "@coverageLimit", pathLimit);

        var paths = new List<string>(Math.Min(pathLimit, 64));
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
            paths.Add(reader.GetString(0));
        return paths;
    }
}
