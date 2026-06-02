using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class TrailingLambdaReferenceExtractor
{
    private const string IdentifierPattern = @"@?[_\p{L}]\w*";

    // Swift / Kotlin trailing-lambda calls such as `items.forEach { ... }`,
    // `list.filter { ... }`, and `animate { ... } completion: { ... }` do not
    // have a trailing `(`, so the shared CallRegex cannot see them.
    private static readonly Regex CallRegex = new(
        $@"(?<![\w$])(?<name>{IdentifierPattern})(?:<[^>\n]+>)?\s*\{{",
        RegexOptions.Compiled);

    public static void EmitReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference)
    {
        foreach (Match match in CallRegex.Matches(preparedLine))
        {
            var callIndex = match.Groups["name"].Index;
            if (IsInheritanceClause(preparedLine, callIndex))
                continue;
            if (IsRightHandSideOfEquality(preparedLine, callIndex))
                continue;

            addCallLikeReference(match.Groups["name"].Value, callIndex);
        }
    }

    private static bool IsInheritanceClause(string preparedLine, int nameIndex)
    {
        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        return probe >= 0 && preparedLine[probe] == ':';
    }

    private static bool IsRightHandSideOfEquality(string preparedLine, int nameIndex)
    {
        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        if (probe < 1 || preparedLine[probe] != '=')
            return false;

        probe--;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        return probe >= 0 && preparedLine[probe] == '=';
    }
}
