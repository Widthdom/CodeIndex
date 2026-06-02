using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class TerraformReferenceExtractor
{
    private readonly record struct ReferencePattern(Regex Regex, bool SkipSpecialReferencePrefix = false);

    // Terraform dotted references are paren-less and therefore invisible to the shared CallRegex.
    // Terraform の dotted reference は括弧を伴わないため、共有 CallRegex では見えない。
    private static readonly Regex VarReferenceRegex = new(
        @"(?<![\w.])var\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex LocalReferenceRegex = new(
        @"(?<![\w.])local\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex ModuleReferenceRegex = new(
        @"(?<![\w.])module\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex ModuleOutputReferenceRegex = new(
        @"(?<![\w.])module\.[A-Za-z_]\w*\.outputs\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex DataReferenceRegex = new(
        @"(?<![\w.])data\.[A-Za-z_]\w*\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly Regex ResourceReferenceRegex = new(
        @"(?<![\w.])(?<type>[A-Za-z_][\w:]*_[A-Za-z_]\w*)\.(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled);

    private static readonly ReferencePattern[] ReferencePatterns =
    [
        new(VarReferenceRegex),
        new(LocalReferenceRegex),
        new(ModuleOutputReferenceRegex),
        new(ModuleReferenceRegex),
        new(DataReferenceRegex),
        new(ResourceReferenceRegex, SkipSpecialReferencePrefix: true),
    ];

    public static void Emit(
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        foreach (var pattern in ReferencePatterns)
            EmitMatches(pattern, preparedLine, context, lineNumber, references, seen, fileId, definitionNames, container);
    }

    private static void EmitMatches(
        ReferencePattern pattern,
        string preparedLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        HashSet<string>? definitionNames,
        SymbolRecord? container)
    {
        foreach (Match match in pattern.Regex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (definitionNames != null && definitionNames.Contains(nameGroup.Value))
                continue;

            if (pattern.SkipSpecialReferencePrefix && IsSpecialReferencePrefix(preparedLine, match.Index))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    private static bool IsSpecialReferencePrefix(string line, int typeStartIndex)
    {
        return HasPrefix(line, typeStartIndex, "var")
            || HasPrefix(line, typeStartIndex, "local")
            || HasPrefix(line, typeStartIndex, "module")
            || HasPrefix(line, typeStartIndex, "data");
    }

    private static bool HasPrefix(string line, int typeStartIndex, string prefix)
    {
        int prefixStart = typeStartIndex - prefix.Length - 1;
        if (prefixStart < 0)
            return false;

        return line.AsSpan(prefixStart, prefix.Length).SequenceEqual(prefix)
            && line[prefixStart + prefix.Length] == '.';
    }
}
