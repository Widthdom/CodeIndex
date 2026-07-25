using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Archives;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ExportImportCommandRunner
{
    private static SqliteCommand CreateCtagsSymbolCommand(SqliteConnection connection, CtagsExportOptions filters)
    {
        var cmd = connection.CreateCommand();
        var sql = $"""
            SELECT
                s.name,
                f.path,
                COALESCE(s.start_line, s.line, 1),
                s.kind,
                f.lang,
                s.container_kind,
                s.container_name,
                s.visibility
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE s.name IS NOT NULL
              AND trim(s.name) != ''
              AND s.kind IS NOT NULL
              AND trim(s.kind) != ''
              AND s.kind IN ({SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds)})
            """;
        AppendCtagsFilters(ref sql, filters);
        sql += " ORDER BY s.name COLLATE NOCASE, f.path, COALESCE(s.start_line, s.line, 1)";
        cmd.CommandText = sql;
        AddCtagsFilterParameters(cmd, filters);
        return cmd;
    }

    private static SqliteCommand CreateCtagsSkipReasonCommand(SqliteConnection connection, CtagsExportOptions filters)
    {
        var cmd = connection.CreateCommand();
        var skipReasonCases = new List<string>
        {
            $"WHEN s.name IS NULL OR trim(s.name) = '' THEN '{CtagsSkipInvalidName}'",
            $"WHEN s.kind IS NULL OR trim(s.kind) = '' OR s.kind NOT IN ({SymbolKindCatalog.ToSqlCheckInList(SymbolKindCatalog.SymbolKinds)}) THEN '{CtagsSkipUnsupportedKind}'",
        };
        if (filters.GeneratedFileFilterAvailable && !filters.IncludeGenerated)
            skipReasonCases.Add($"WHEN COALESCE(f.generated, 0) != 0 THEN '{CtagsSkipGeneratedCode}'");
        if (!string.IsNullOrWhiteSpace(filters.Lang))
            skipReasonCases.Add($"WHEN COALESCE(f.lang, '') != @lang THEN '{CtagsSkipLanguageFilter}'");
        if (filters.ExcludeTests)
            skipReasonCases.Add($"WHEN {DbReader.TestPathCondition} THEN '{CtagsSkipTestFilter}'");
        if (filters.PathPatterns.Count > 0)
        {
            var pathPredicates = new List<string>(filters.PathPatterns.Count);
            for (var i = 0; i < filters.PathPatterns.Count; i++)
                pathPredicates.Add(DbReader.BuildPathFilterPredicate("f", "pathPattern", i, filters.PathPatterns[i]));
            skipReasonCases.Add($"WHEN NOT ({string.Join(" OR ", pathPredicates)}) THEN '{CtagsSkipPathFilter}'");
        }
        if (filters.ExcludePathPatterns.Count > 0)
        {
            var excludePathPredicates = new List<string>(filters.ExcludePathPatterns.Count);
            for (var i = 0; i < filters.ExcludePathPatterns.Count; i++)
                excludePathPredicates.Add(DbReader.BuildPathFilterPredicate("f", "excludePathPattern", i, filters.ExcludePathPatterns[i]));
            skipReasonCases.Add($"WHEN ({string.Join(" OR ", excludePathPredicates)}) THEN '{CtagsSkipExcludePathFilter}'");
        }

        cmd.CommandText = $"""
            SELECT skip_reason, COUNT(*)
            FROM (
                SELECT
                    CASE
                        {string.Join(Environment.NewLine + "                        ", skipReasonCases)}
                        ELSE NULL
                    END AS skip_reason
                FROM symbols s
                JOIN files f ON s.file_id = f.id
            )
            WHERE skip_reason IS NOT NULL
            GROUP BY skip_reason
            """;
        AddCtagsFilterParameters(cmd, filters);
        return cmd;
    }

    private static Dictionary<string, long> CountCtagsSkipReasons(SqliteConnection connection, CtagsExportOptions filters)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [CtagsSkipInvalidName] = 0,
            [CtagsSkipUnsupportedKind] = 0,
            [CtagsSkipGeneratedCode] = 0,
            [CtagsSkipLanguageFilter] = 0,
            [CtagsSkipTestFilter] = 0,
            [CtagsSkipPathFilter] = 0,
            [CtagsSkipExcludePathFilter] = 0,
            [CtagsSkipOther] = 0,
        };
        using var cmd = CreateCtagsSkipReasonCommand(connection, filters);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var reason = reader.GetString(0);
            var boundedReason = counts.ContainsKey(reason) ? reason : CtagsSkipOther;
            counts[boundedReason] += reader.GetInt64(1);
        }
        return counts;
    }

    private static void AppendCtagsFilters(ref string sql, CtagsExportOptions filters)
    {
        if (filters.GeneratedFileFilterAvailable && !filters.IncludeGenerated)
            sql += " AND COALESCE(f.generated, 0) = 0";

        if (!string.IsNullOrWhiteSpace(filters.Lang))
            sql += " AND f.lang = @lang";

        if (filters.ExcludeTests)
            sql += $" AND NOT {DbReader.TestPathCondition}";

        if (filters.PathPatterns.Count > 0)
        {
            var pathPredicates = new List<string>(filters.PathPatterns.Count);
            for (var i = 0; i < filters.PathPatterns.Count; i++)
                pathPredicates.Add(DbReader.BuildPathFilterPredicate("f", "pathPattern", i, filters.PathPatterns[i]));
            sql += " AND (" + string.Join(" OR ", pathPredicates) + ")";
        }

        for (var i = 0; i < filters.ExcludePathPatterns.Count; i++)
            sql += $" AND NOT {DbReader.BuildPathFilterPredicate("f", "excludePathPattern", i, filters.ExcludePathPatterns[i])}";
    }

    private static void AddCtagsFilterParameters(SqliteCommand cmd, CtagsExportOptions filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.Lang))
            SqliteCommandPolicy.Add(cmd, "@lang", filters.Lang);

        DbReader.AddPathFilterParameterSet(cmd, "pathPattern", filters.PathPatterns);
        DbReader.AddPathFilterParameterSet(cmd, "excludePathPattern", filters.ExcludePathPatterns);
    }

    private static void AppendCtagsExtensionField(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        builder
            .Append('\t')
            .Append(name)
            .Append(':')
            .Append(SanitizeCtagsField(value));
    }

}
