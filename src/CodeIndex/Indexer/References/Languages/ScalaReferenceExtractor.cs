using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class ScalaReferenceExtractor
{
    private const string FunctionalIdentifierPattern = @"@?[_\p{L}\$][\w$]*";

    // Scala's `name { ... }` / `name { x => ... }` block-call form does not use
    // trailing `(`, so the shared CallRegex cannot see it.
    private static readonly Regex TrailingBlockCallRegex = new(
        $@"(?<![\w$])(?<name>{FunctionalIdentifierPattern})(?:\[[^\]\n]+\])?\s*\{{",
        RegexOptions.Compiled);
    private static readonly Regex ForGeneratorRegex = new(
        $@"<-\s*(?<name>{FunctionalIdentifierPattern})",
        RegexOptions.Compiled);
    private static readonly Regex ImplicitConversionRegex = new(
        $@"^\s*implicit\s+def\s+{FunctionalIdentifierPattern}\s*\([^:]+:\s*(?<source>[A-Z]\w*(?:\[[^\]\n]+\])?)\s*\)\s*:\s*(?<target>[A-Z]\w*(?:\[[^\]\n]+\])?)",
        RegexOptions.Compiled);
    private static readonly Regex ImplicitClassRegex = new(
        $@"^\s*implicit\s+class\s+{FunctionalIdentifierPattern}\s*\([^:]+:\s*(?<source>[A-Z]\w*(?:\[[^\]\n]+\])?)",
        RegexOptions.Compiled);
    private static readonly Regex GivenRegex = new(
        @"^\s*given(?:\s+[A-Za-z_]\w*)?\s*(?::|as)\s*(?<type>[A-Z]\w*(?:\[[^\]\n]+\])?)",
        RegexOptions.Compiled);
    private static readonly Regex UsingClauseRegex = new(
        @"\busing\s*\((?<params>[^)]*)\)",
        RegexOptions.Compiled);
    private static readonly Regex UsingTypeRegex = new(
        @"(?::|<:|=>)\s*(?<type>[A-Z]\w*(?:\[[^\]\n]+\])?)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> IgnoredBlockCallNames = new(StringComparer.Ordinal)
    {
        "match", "catch", "else", "finally",
    };

    public static void EmitTrailingBlockCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference)
    {
        foreach (Match match in TrailingBlockCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (IgnoredBlockCallNames.Contains(name))
                continue;

            addCallLikeReference(name, match.Groups["name"].Index);
        }
    }

    public static void EmitAdditionalReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        Action<string, int> addCallLikeReference)
    {
        foreach (Match match in ForGeneratorRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            addCallLikeReference(nameGroup.Value, nameGroup.Index);
        }

        EmitConversionTypeReferences(ImplicitConversionRegex.Match(preparedLine));
        EmitConversionTypeReferences(ImplicitClassRegex.Match(preparedLine));

        var givenMatch = GivenRegex.Match(preparedLine);
        if (givenMatch.Success)
            AddTypeReference(givenMatch.Groups["type"]);

        foreach (Match usingMatch in UsingClauseRegex.Matches(preparedLine))
        {
            var parameters = usingMatch.Groups["params"];
            foreach (Match typeMatch in UsingTypeRegex.Matches(parameters.Value))
                AddTypeReference(typeMatch.Groups["type"], parameters.Index);
        }

        void EmitConversionTypeReferences(Match match)
        {
            if (!match.Success)
                return;

            AddTypeReference(match.Groups["source"]);
            var target = match.Groups["target"];
            if (target.Success)
                AddTypeReference(target);
        }

        void AddTypeReference(Group group, int baseIndex = 0)
        {
            if (!group.Success || group.Value.Length == 0)
                return;

            ReferenceExtractor.AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                group.Value,
                baseIndex + group.Index,
                context,
                lineNumber,
                resolveContainerForColumn(baseIndex + group.Index),
                "scala");
        }
    }

    public static void EmitMethodReferenceReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
        => JvmMethodReferenceExtractor.EmitMethodReferenceReferences(
            "scala",
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
}
