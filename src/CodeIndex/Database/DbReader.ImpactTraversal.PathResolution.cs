using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    private ImpactPathNode ResolveImpactPathNode(
        string name,
        long? symbolId,
        string? kind,
        string? lang,
        string? referencePath,
        int? referenceLine)
    {
        var node = TryResolveImpactPathNodeDefinition(name, symbolId, kind, lang, referencePath)
            ?? new ImpactPathNode
            {
                SymbolId = symbolId,
                Name = name,
                Kind = kind,
                Lang = lang,
            };
        node.ReferencePath = referencePath;
        node.ReferenceLine = referenceLine;
        return node;
    }

    private ImpactPathNode? TryResolveImpactPathNodeDefinition(
        string name,
        long? symbolId,
        string? kind,
        string? lang,
        string? preferredPath)
    {
        if (!_symbolColumns.Contains("name") || !_symbolColumns.Contains("kind"))
            return null;

        using var cmd = _conn.CreateCommand();
        var containerNameSql = GetSymbolColumnSql("container_name");
        var containerQualifiedNameSql = GetSymbolColumnSql("container_qualified_name");
        var familyKeySql = GetSymbolColumnSql("family_key");
        var namePredicate = _foldReady && _symbolColumns.Contains("name_folded")
            ? "s.name_folded = @nameFolded"
            : "s.name = @name COLLATE NOCASE";

        cmd.CommandText = $@"
            SELECT s.id,
                   f.path,
                   f.lang,
                   s.kind,
                   s.name,
                   s.line,
                   {containerNameSql} AS container_name,
                   {containerQualifiedNameSql} AS container_qualified_name,
                   {familyKeySql} AS family_key,
                   s.file_id,
                   COUNT(*) OVER () AS matching_definition_count
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE ((@symbolId IS NOT NULL AND s.id = @symbolId)
                   OR (@symbolId IS NULL AND {namePredicate}))
              AND s.kind NOT IN ('import', 'namespace')
              AND (@kind IS NULL OR s.kind = @kind)
              AND (@lang IS NULL OR f.lang = @lang)
            ORDER BY CASE WHEN @preferredPath IS NOT NULL AND f.path = @preferredPath THEN 0 ELSE 1 END,
                     f.path,
                     s.line
            LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@name", name);
        SqliteCommandPolicy.AddNullableInt64(cmd, "@symbolId", symbolId);
        if (_foldReady && _symbolColumns.Contains("name_folded"))
            SqliteCommandPolicy.Add(cmd, "@nameFolded", NameFold.Fold(name) ?? name);
        SqliteCommandPolicy.AddNullableText(cmd, "@kind", kind);
        SqliteCommandPolicy.AddNullableText(cmd, "@lang", lang);
        SqliteCommandPolicy.AddNullableText(cmd, "@preferredPath", preferredPath);

        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return null;

        var definitionSymbolId = reader.GetInt64(0);
        var definitionPath = reader.GetString(1);
        var definitionLang = GetNullableString(reader, 2);
        var definitionKind = reader.GetString(3);
        var definitionName = reader.GetString(4);
        var definitionLine = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
        var containerName = GetNullableString(reader, 6);
        var containerQualifiedName = GetNullableString(reader, 7);
        var familyKey = GetNullableString(reader, 8);
        var fileId = reader.GetInt64(9);
        var matchingDefinitionCount = reader.GetInt64(10);

        return new ImpactPathNode
        {
            SymbolId = symbolId ?? (matchingDefinitionCount == 1 ? definitionSymbolId : null),
            Name = definitionName,
            Kind = definitionKind,
            Lang = definitionLang,
            DefinitionPath = definitionPath,
            DefinitionLine = definitionLine,
            Container = containerName,
            FamilyKey = familyKey,
            LogicalTargetKey = BuildImpactPathLogicalTargetKey(
                definitionLang,
                definitionKind,
                familyKey,
                containerQualifiedName,
                fileId),
        };
    }

    private static string BuildImpactPathLogicalTargetKey(
        string? lang,
        string kind,
        string? familyKey,
        string? containerQualifiedName,
        long fileId)
    {
        if (!string.IsNullOrWhiteSpace(familyKey))
            return $"family|{lang ?? string.Empty}|{kind}|{familyKey}";
        if (!string.IsNullOrWhiteSpace(containerQualifiedName))
            return $"container|{fileId}|{kind}|{containerQualifiedName}";
        return $"file|{fileId}";
    }
}
