using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class KotlinSymbolNameNormalizer
{
    internal static string Normalize(string name, string matchLine)
    {
        var normalizedName = StripBacktickIdentifier(name);
        var trimmedLine = matchLine.AsSpan().TrimStart();
        if (!trimmedLine.StartsWith("companion object".AsSpan(), StringComparison.Ordinal))
            return normalizedName;

        var trimmedName = normalizedName.AsSpan().Trim();
        return trimmedName.IsEmpty
            || trimmedName.Equals("companion object".AsSpan(), StringComparison.Ordinal)
            ? "Companion"
            : normalizedName;
    }

    private static string StripBacktickIdentifier(string name)
    {
        if (name.Length >= 2 && name[0] == '`' && name[^1] == '`')
            return name[1..^1];
        return name;
    }

    internal static void NormalizeSecondaryConstructorNames(List<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function"
                || symbol.ContainerKind != "class"
                || string.IsNullOrWhiteSpace(symbol.ContainerName))
            {
                continue;
            }

            var signature = symbol.Signature.AsSpan().TrimStart();
            if (signature.IsEmpty)
                continue;

            var isSecondaryConstructor = signature.StartsWith("constructor".AsSpan(), StringComparison.Ordinal)
                || signature.StartsWith("public constructor".AsSpan(), StringComparison.Ordinal)
                || signature.StartsWith("private constructor".AsSpan(), StringComparison.Ordinal)
                || signature.StartsWith("protected constructor".AsSpan(), StringComparison.Ordinal)
                || signature.StartsWith("internal constructor".AsSpan(), StringComparison.Ordinal);
            if (!isSecondaryConstructor)
                continue;

            symbol.Name = symbol.ContainerName;
        }
    }
}
