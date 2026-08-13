using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbReader
{
    private string BuildQualifiedSymbolMatchSql(string parameterStem, bool useFoldedName, string symbolAlias = "s", string fileAlias = "f")
    {
        var containerNameSql = GetSymbolColumnSql("container_name", "''", symbolAlias);
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name", containerNameSql, symbolAlias);
        var nameMatchSql = useFoldedName
            ? $"{symbolAlias}.name_folded = @{parameterStem}CSharpLeafFolded"
            : $"{symbolAlias}.name = @{parameterStem}CSharpLeaf COLLATE NOCASE";
        var qualifiedNameMatchSql = useFoldedName
            ? $"{symbolAlias}.name_folded = @{parameterStem}CSharpQualifiedFolded"
            : $"{symbolAlias}.name = @{parameterStem}CSharpQualified COLLATE NOCASE";
        return $@"({fileAlias}.lang = 'csharp'
                  AND ({qualifiedNameMatchSql}
                       OR ({nameMatchSql}
                           AND ({containerNameSql} = @{parameterStem}Container COLLATE NOCASE
                                OR {containerQualifiedNameSql} = @{parameterStem}Container COLLATE NOCASE
                                OR {containerQualifiedNameSql} COLLATE NOCASE LIKE @{parameterStem}ContainerSuffixLike ESCAPE '\'))))";
    }

    private string BuildExactPrimarySymbolNameMatchSql(
        string parameterSql,
        bool useFoldedName,
        string query,
        string? lang)
    {
        var matchSql = useFoldedName
            ? BuildPersistedFoldedNameMatchSql("s.name_folded", parameterSql)
            : $"s.name = {parameterSql} COLLATE NOCASE";
        if (!string.IsNullOrWhiteSpace(lang) || !SqlNameResolver.HasQualifier(query))
            return matchSql;

        // Preserve direct qualified matching for ordinary and legacy C# rows. Only v3
        // explicit-interface rows have a display alias; those rows must use the C# identity
        // clause so `IFoo.this` cannot also match a distinct `IFoo.@this` implementation.
        // 通常および legacy C# row の修飾直接一致は維持する。表示 alias を持つ v3 の
        // 明示的 interface row だけを C# identity 条件へ限定し、`IFoo.this` が別の
        // `IFoo.@this` 実装にも一致しないようにする。
        var displayNameFoldedSql = GetSymbolColumnSql("display_name_folded", "NULL");
        return $"((f.lang <> 'csharp' OR {displayNameFoldedSql} IS NULL) AND {matchSql})";
    }

    private string BuildCSharpExplicitInterfaceIdentityMatchSql(
        string parameterStem,
        string symbolAlias = "s",
        string fileAlias = "f")
    {
        if (!_csharpSymbolNameContractCurrent
            || !_foldMetadataCurrent
            || !_symbolColumns.Contains("name_folded"))
            return "0";

        return $"({fileAlias}.lang = 'csharp' AND {symbolAlias}.name_folded = @{parameterStem}CSharpExplicitInterfaceIdentityFolded)";
    }

    private string BuildCSharpExplicitInterfaceShortAliasMatchSql(
        string parameterStem,
        string symbolAlias = "s",
        string fileAlias = "f")
        => _foldReady
            && _csharpSymbolNameContractCurrent
            && _symbolColumns.Contains("display_name_folded")
            && HasSymbolIndex("idx_symbols_display_name_folded")
            ? $"({fileAlias}.lang = 'csharp' AND {symbolAlias}.display_name_folded = @{parameterStem}LeafFolded)"
            : $"({fileAlias}.lang = 'csharp' AND {symbolAlias}.name = @{parameterStem}Leaf COLLATE NOCASE)";

    private static void AddCSharpExplicitInterfaceIdentityQueryParameter(
        SqliteCommand cmd,
        string parameterStem,
        string query)
    {
        SqliteCommandPolicy.Add(
            cmd,
            $"@{parameterStem}CSharpExplicitInterfaceIdentityFolded",
            CSharpSymbolNameNormalizer.NormalizeExplicitInterfaceQueryIdentityNameFolded(query));
    }

    private static string GetQualifiedQueryContainer(string query)
    {
        var normalized = SqlNameResolver.NormalizeQualifiedName(query);
        var lastDot = normalized.LastIndexOf('.');
        return lastDot > 0 ? normalized[..lastDot] : string.Empty;
    }

    private static string GetQualifiedQuerySuffix(string query)
    {
        var normalized = SqlNameResolver.NormalizeQualifiedName(query);
        var lastDot = normalized.LastIndexOf('.');
        if (lastDot <= 0)
            return normalized;
        var previousDot = normalized.LastIndexOf('.', lastDot - 1);
        return previousDot >= 0 ? normalized[(previousDot + 1)..] : normalized;
    }

    private static string GetQualifiedQueryLeaf(string query, string? lang)
    {
        var leaf = SqlNameResolver.GetLeafName(query);
        return NormalizeCSharpVerbatimQuery(leaf, lang) ?? leaf;
    }

    private static void AddQualifiedSymbolQueryParameters(SqliteCommand cmd, string parameterStem, string query)
    {
        var csharpDisplayQuery =
            CSharpSymbolNameNormalizer.NormalizeExplicitInterfaceQueryDisplayName(query);
        var csharpQualifiedQuery =
            NormalizeCSharpVerbatimQuery(csharpDisplayQuery, "csharp")
            ?? csharpDisplayQuery;
        var container = GetQualifiedQueryContainer(csharpQualifiedQuery);
        var csharpLeaf = GetQualifiedQueryLeaf(csharpQualifiedQuery, "csharp");
        SqliteCommandPolicy.Add(cmd, $"@{parameterStem}Container", container);
        SqliteCommandPolicy.Add(cmd, $"@{parameterStem}ContainerSuffixLike", $"%.{EscapeLikeQuery(container)}");
        SqliteCommandPolicy.Add(cmd, $"@{parameterStem}CSharpQualified", csharpQualifiedQuery);
        SqliteCommandPolicy.Add(
            cmd,
            $"@{parameterStem}CSharpQualifiedFolded",
            NameFold.Fold(csharpQualifiedQuery) ?? csharpQualifiedQuery);
        SqliteCommandPolicy.Add(cmd, $"@{parameterStem}CSharpLeaf", csharpLeaf);
        SqliteCommandPolicy.Add(
            cmd,
            $"@{parameterStem}CSharpLeafFolded",
            NameFold.Fold(csharpLeaf) ?? csharpLeaf);
    }

    private bool HasSingleQualifiedSymbolDefinition(string query, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (!SqlNameResolver.HasQualifier(query))
            return false;

        var matches = SearchSymbols(query, 2, kind: null, lang, pathPatterns: null, excludePathPatterns: null, excludeTests, since: null, exact: false);
        if (matches.Count != 1)
            return false;

        var leafMatches = SearchSymbols(SqlNameResolver.GetLeafName(query), 2, kind: null, lang, pathPatterns: null, excludePathPatterns: null, excludeTests, since: null, exact: true);
        return leafMatches.Count == 1;
    }

    private bool HasQualifiedSymbolDefinition(string query, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (!SqlNameResolver.HasQualifier(query))
            return false;

        return SearchSymbols(query, 1, kind: null, lang, pathPatterns: null, excludePathPatterns: null, excludeTests, since: null, exact: false).Count > 0;
    }

    private static string BuildQualifiedContextMatchSql(string contextSql, string columnSql, bool folded, bool like)
    {
        var functionName = (folded, like) switch
        {
            (true, true) => "sql_context_like_name_folded_at",
            (true, false) => "sql_context_has_name_folded_at",
            (false, true) => "sql_context_like_name_at",
            _ => "sql_context_has_name_at",
        };
        return $"({functionName}({contextSql}, @aliasQuery, {columnSql}) = 1 OR {functionName}({contextSql}, @aliasQuerySuffix, {columnSql}) = 1)";
    }

    private static string BuildQualifiedLeafFallbackSql(string nameSql, string foldedNameSql, bool folded)
        => folded
            ? $"(@allowQualifiedLeafFallback = 1 AND f.lang = 'csharp' AND {foldedNameSql} = @aliasQueryLeafFolded)"
            : $"(@allowQualifiedLeafFallback = 1 AND f.lang = 'csharp' AND {nameSql} = @aliasQueryLeaf COLLATE NOCASE)";

    private static string BuildCSharpQualifiedContextFallbackSql(string qualifiedContextSql)
        => $"(@allowCSharpQualifiedContextMatch = 1 AND f.lang = 'csharp' AND {qualifiedContextSql})";

    private static void AddQualifiedGraphQueryParameters(SqliteCommand cmd, string query, bool allowLeafFallback, bool allowCSharpContextMatch = false)
    {
        SqliteCommandPolicy.Add(cmd, "@aliasQuerySuffix", GetQualifiedQuerySuffix(query));
        SqliteCommandPolicy.Add(cmd, "@aliasQueryLeaf", SqlNameResolver.GetLeafName(query));
        SqliteCommandPolicy.Add(cmd, "@allowQualifiedLeafFallback", allowLeafFallback ? 1 : 0);
        SqliteCommandPolicy.Add(cmd, "@allowCSharpQualifiedContextMatch", allowCSharpContextMatch ? 1 : 0);
    }
}
