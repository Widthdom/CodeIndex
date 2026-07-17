namespace CodeIndex.Database;

internal static class LogicalPartialSymbolGrouper
{
    private const char KeySeparator = '\u001f';

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
        if (!IsLogicalPartialKind(symbol.Kind) || string.IsNullOrWhiteSpace(symbol.ContainerName))
        {
            key = string.Empty;
            return false;
        }

        key = string.Join(
            KeySeparator,
            symbol.Lang?.ToLowerInvariant() ?? string.Empty,
            symbol.Kind.ToLowerInvariant(),
            symbol.Name,
            symbol.ContainerKind ?? string.Empty,
            symbol.ContainerName);
        return true;
    }

    private static bool IsLogicalPartialKind(string kind)
        => kind is "class" or "struct" or "interface" or "record";
}
