namespace CodeIndex.Database;

internal static class LogicalPartialSymbolGrouper
{
    private const char KeySeparator = '\u001f';

    internal static string BuildSqlKeyExpression(
        string languageSql,
        string kindSql,
        string nameSql,
        string symbolIdSql,
        string signatureSql,
        string containerNameSql,
        string containerQualifiedNameSql,
        string familyKeySql)
    {
        var persistedFamilySql = $"NULLIF(TRIM({familyKeySql}), '')";
        var fallbackContainerSql = $"COALESCE(NULLIF(TRIM({containerQualifiedNameSql}), ''), NULLIF(TRIM({containerNameSql}), ''), '')";
        var normalizedSignatureSql = $"REPLACE(REPLACE(REPLACE(LOWER(COALESCE({signatureSql}, '')), CHAR(9), ' '), CHAR(10), ' '), CHAR(13), ' ')";
        var partialDeclarationSql = $"INSTR(' ' || {normalizedSignatureSql} || ' ', ' partial ') > 0";
        return $@"CASE
            WHEN {languageSql} = 'csharp'
             AND {kindSql} IN ('class', 'struct', 'interface', 'record')
             AND ({persistedFamilySql} IS NOT NULL OR {partialDeclarationSql})
            THEN 'family:' || {languageSql} || CHAR(31) || {kindSql} || CHAR(31) ||
                 COALESCE({persistedFamilySql}, {fallbackContainerSql} || CHAR(31) || {nameSql})
            ELSE 'symbol:' || {symbolIdSql}
        END";
    }

    public static List<T> Group<T>(IReadOnlyList<T> symbols)
        where T : SymbolResult
    {
        if (symbols.Count <= 1)
            return symbols.ToList();

        var groups = symbols
            .Select(symbol => (symbol, key: TryBuildKey(symbol, out var key) ? key : null))
            .Where(item => item.key != null)
            .GroupBy(item => item.key!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Select(item => item.symbol).ToList(), StringComparer.Ordinal);
        if (groups.Count == 0)
            return symbols.ToList();

        var emittedKeys = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<T>(symbols.Count);
        foreach (var symbol in symbols)
        {
            if (!TryBuildKey(symbol, out var key) || !groups.TryGetValue(key, out var group))
            {
                results.Add(symbol);
                continue;
            }
            if (!emittedKeys.Add(key))
                continue;

            var representative = group
                .OrderBy(result => result.Path, StringComparer.Ordinal)
                .ThenBy(result => result.StartLine)
                .First();
            representative.DefinitionSites = group.Count;
            results.Add(representative);
        }

        return results;
    }

    public static bool TryBuildKey(SymbolResult symbol, out string key)
    {
        if (!string.IsNullOrWhiteSpace(symbol.LogicalPartialKey)
            && symbol.LogicalPartialKey.StartsWith("family:", StringComparison.Ordinal))
        {
            key = symbol.LogicalPartialKey;
            return true;
        }

        if (!string.Equals(symbol.Lang, "csharp", StringComparison.OrdinalIgnoreCase)
            || !IsLogicalPartialKind(symbol.Kind)
            || string.IsNullOrWhiteSpace(symbol.Signature)
            || !ContainsPartialModifier(symbol.Signature))
        {
            key = string.Empty;
            return false;
        }

        var containerIdentity = symbol.ContainerQualifiedName ?? symbol.ContainerName ?? string.Empty;
        key = string.Join(
            KeySeparator,
            symbol.Lang?.ToLowerInvariant() ?? string.Empty,
            symbol.Kind.ToLowerInvariant(),
            symbol.Name,
            containerIdentity);
        return true;
    }

    private static bool ContainsPartialModifier(string signature)
    {
        var tokens = signature.Split(
            [' ', '\t', '\r', '\n', '(', ')', '[', ']', '{', '}', ':'],
            StringSplitOptions.RemoveEmptyEntries);
        return tokens.Contains("partial", StringComparer.Ordinal);
    }

    private static bool IsLogicalPartialKind(string kind)
        => kind is "class" or "struct" or "interface" or "record";
}
