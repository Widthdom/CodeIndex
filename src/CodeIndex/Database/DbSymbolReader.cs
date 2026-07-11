using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Database;

/// <summary>
/// Symbol query operations: search, definitions, outline, analyze (partial class split from DbReader.cs).
/// シンボルクエリ操作: 検索、定義、アウトライン、分析（DbReader.csからのpartial class分割）。
/// </summary>
public partial class DbReader
{
    internal const int DefinitionBodyMaxLines = 20;
    internal const int DefinitionBodyMaxRequestedLines = 1_000;
    internal const int DefinitionBodyMaxBytes = 16 * 1024;

    private const string SymbolLanguageFileIdFilter = " AND s.file_id IN (SELECT id FROM files WHERE lang = @lang)";
    private const int QueryOutputSignatureMaxChars = 512;
    private const string QueryOutputSignatureTruncationSuffix = "...";
    private void AppendVisibilityFilters(ref string sql, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var expandedVisibilityFilters = visibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(visibilityFilters) : null;
        var expandedExcludeVisibilityFilters = excludeVisibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(excludeVisibilityFilters) : null;
        EnsureVisibilityFilterParameterBudget(expandedVisibilityFilters, expandedExcludeVisibilityFilters);

        if (expandedVisibilityFilters is { Count: > 0 })
            sql += $" AND lower({GetSymbolColumnSql("visibility", "''")}) IN ({SqliteDynamicSql.BuildParameterList("visibility", expandedVisibilityFilters.Count)})";
        if (expandedExcludeVisibilityFilters is { Count: > 0 })
            sql += $" AND lower({GetSymbolColumnSql("visibility", "''")}) NOT IN ({SqliteDynamicSql.BuildParameterList("excludeVisibility", expandedExcludeVisibilityFilters.Count)})";
    }

    private static void AddVisibilityFilterParameters(SqliteCommand cmd, IReadOnlyList<string>? visibilityFilters, IReadOnlyList<string>? excludeVisibilityFilters)
    {
        var expandedVisibilityFilters = visibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(visibilityFilters) : null;
        var expandedExcludeVisibilityFilters = excludeVisibilityFilters is { Count: > 0 } ? ExpandVisibilityFilterValues(excludeVisibilityFilters) : null;
        EnsureVisibilityFilterParameterBudget(expandedVisibilityFilters, expandedExcludeVisibilityFilters);

        if (expandedVisibilityFilters is { Count: > 0 })
            SqliteDynamicSql.AddParameters(cmd, "visibility", expandedVisibilityFilters, SqliteType.Text, "visibility filters");

        if (expandedExcludeVisibilityFilters is { Count: > 0 })
            SqliteDynamicSql.AddParameters(cmd, "excludeVisibility", expandedExcludeVisibilityFilters, SqliteType.Text, "visibility filters");
    }

    private static void EnsureVisibilityFilterParameterBudget(IReadOnlyCollection<string>? visibilityFilters, IReadOnlyCollection<string>? excludeVisibilityFilters)
        => SqliteDynamicSql.EnsureParameterBudget((visibilityFilters?.Count ?? 0) + (excludeVisibilityFilters?.Count ?? 0), "visibility filters");

    private static List<string> ExpandVisibilityFilterValues(IReadOnlyList<string> filters)
    {
        var expanded = new List<string>();
        foreach (var filter in filters)
        {
            string[] aliases = filter switch
            {
                "public" => ["public", "pub", "open", "export"],
                "private" => ["private", "fileprivate"],
                _ => [filter],
            };

            foreach (var alias in aliases)
            {
                if (!expanded.Contains(alias, StringComparer.Ordinal))
                    expanded.Add(alias);
            }
        }

        return expanded;
    }

    private static string BuildSameFilePrivateUseExclusionSql(string symbolAlias, string fileAlias, string visibilitySql, string startLineSql, string endLineSql)
    {
        return $@"
              AND NOT (
                  {fileAlias}.lang = 'csharp'
                  AND {visibilitySql} IN ('private', 'fileprivate')
                  AND {symbolAlias}.name <> ''
                  AND EXISTS (
                      SELECT 1
                      FROM chunks same_file_chunk
                      WHERE same_file_chunk.file_id = {symbolAlias}.file_id
                        AND csharp_identifier_occurrence_count(same_file_chunk.content, {symbolAlias}.name) > 0
                        AND (
                            same_file_chunk.end_line < {startLineSql}
                            OR same_file_chunk.start_line > {endLineSql}
                            OR csharp_identifier_occurrence_count(same_file_chunk.content, {symbolAlias}.name) > 1
                        )
                  )
              )";
    }

    private string BuildCSharpPartialContainingTypeUseExclusionSql(string symbolAlias, string fileAlias, string visibilitySql)
    {
        var containerKindSql = GetSymbolColumnSql("container_kind", "''", symbolAlias);
        var containerNameSql = GetSymbolColumnSql("container_name", "''", symbolAlias);
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", containerNameSql, symbolAlias);
        var ownContainerNameSql = GetSymbolColumnSql("container_name", "''", "partial_own_type");
        var ownSignatureSql = GetSymbolColumnSql("signature", "''", "partial_own_type");
        var peerContainerNameSql = GetSymbolColumnSql("container_name", "''", "partial_peer_type");
        var peerSignatureSql = GetSymbolColumnSql("signature", "''", "partial_peer_type");
        var ownQualifiedNameSql = $@"CASE
                            WHEN {ownContainerNameSql} <> '' THEN {ownContainerNameSql} || '.' || partial_own_type.name
                            ELSE partial_own_type.name
                        END";
        var peerQualifiedNameSql = $@"CASE
                            WHEN {peerContainerNameSql} <> '' THEN {peerContainerNameSql} || '.' || partial_peer_type.name
                            ELSE partial_peer_type.name
                        END";

        return $@"
              AND NOT (
                  {fileAlias}.lang = 'csharp'
                  AND {visibilitySql} IN ('private', 'fileprivate')
                  AND {symbolAlias}.name <> ''
                  AND {containerKindSql} IN ('class', 'struct', 'interface')
                  AND {containerNameSql} <> ''
                  AND EXISTS (
                      SELECT 1
                      FROM symbols partial_own_type
                      WHERE partial_own_type.file_id = {symbolAlias}.file_id
                        AND partial_own_type.kind = {containerKindSql}
                        AND partial_own_type.name = {containerNameSql}
                        AND lower({ownSignatureSql}) LIKE '%partial%'
                        AND (
                            {containerQualifiedNameSql} = ''
                            OR {containerQualifiedNameSql} = partial_own_type.name
                            OR {containerQualifiedNameSql} = {ownQualifiedNameSql}
                        )
                  )
                  AND EXISTS (
                      SELECT 1
                      FROM symbols partial_peer_type
                      JOIN files partial_peer_file ON partial_peer_file.id = partial_peer_type.file_id
                      JOIN chunks partial_peer_chunk ON partial_peer_chunk.file_id = partial_peer_type.file_id
                      WHERE partial_peer_file.lang = 'csharp'
                        AND partial_peer_type.file_id <> {symbolAlias}.file_id
                        AND partial_peer_type.kind = {containerKindSql}
                        AND partial_peer_type.name = {containerNameSql}
                        AND lower({peerSignatureSql}) LIKE '%partial%'
                        AND (
                            {containerQualifiedNameSql} = ''
                            OR {containerQualifiedNameSql} = partial_peer_type.name
                            OR {containerQualifiedNameSql} = {peerQualifiedNameSql}
                        )
                        AND csharp_identifier_occurrence_count(partial_peer_chunk.content, {symbolAlias}.name) > 0
                      LIMIT 1
                  )
              )";
    }

    private static void ApplyQueryOutputSignatureLimits(IEnumerable<SymbolResult> symbols)
    {
        foreach (var symbol in symbols)
            ApplyQueryOutputSignatureLimit(symbol);
    }

    private static void ApplyQueryOutputSignatureLimit(SymbolResult symbol)
    {
        if (!TryTruncateQueryOutputSignature(symbol.Signature, out var signature, out var originalLength))
            return;

        symbol.Signature = signature;
        symbol.SignatureTruncated = true;
        symbol.SignatureOriginalLength = originalLength;
    }

    private static bool TryTruncateQueryOutputSignature(string? signature, out string? truncatedSignature, out int? originalLength)
    {
        truncatedSignature = signature;
        originalLength = null;
        if (signature == null || signature.Length <= QueryOutputSignatureMaxChars)
            return false;

        originalLength = signature.Length;
        truncatedSignature = signature[..(QueryOutputSignatureMaxChars - QueryOutputSignatureTruncationSuffix.Length)]
            + QueryOutputSignatureTruncationSuffix;
        return true;
    }
}
