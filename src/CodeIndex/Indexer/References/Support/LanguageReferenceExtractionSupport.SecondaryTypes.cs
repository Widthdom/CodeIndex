using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static void EmitPascalTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        if (StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "uses"))
        {
            var usesMatch = PascalUsesRegex.Match(preparedLine);
            if (usesMatch.Success)
                EmitCommaSeparatedNames(usesMatch.Groups["list"].Value, usesMatch.Groups["list"].Index, "pascal", references, seen, fileId, context, lineNumber, container);
        }

        var hasPascalBaseMarker = preparedLine.IndexOf('=') >= 0
            && preparedLine.IndexOf('(') >= 0
            && preparedLine.IndexOf(')') >= 0
            && (ContainsKeywordIgnoringCase(preparedLine, "class")
                || ContainsKeywordIgnoringCase(preparedLine, "interface")
                || ContainsKeywordIgnoringCase(preparedLine, "object"));
        if (hasPascalBaseMarker)
        {
            foreach (Match match in PascalClassBaseRegex.Matches(preparedLine))
                EmitCommaSeparatedNames(match.Groups["bases"].Value, match.Groups["bases"].Index, "pascal", references, seen, fileId, context, lineNumber, resolveContainerForColumn(match.Groups["bases"].Index));
        }

        if (preparedLine.IndexOf(':') < 0)
            return;

        foreach (Match match in PascalTypeAfterColonRegex.Matches(preparedLine))
        {
            if (!IsPascalColonTypeReferenceContext(preparedLine, lineNumber, container))
                continue;

            var group = match.Groups["type"];
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "pascal");
        }
    }

    private static bool IsPascalColonTypeReferenceContext(string preparedLine, int lineNumber, SymbolRecord? container)
    {
        var trimmed = preparedLine.TrimStart();
        if (container?.Kind != "function"
            || !container.BodyStartLine.HasValue
            || lineNumber < container.BodyStartLine.Value)
        {
            return true;
        }

        return StartsWithPascalDeclarationKeyword(trimmed);
    }

    private static bool StartsWithPascalDeclarationKeyword(string trimmedLine)
    {
        foreach (var keyword in PascalDeclarationKeywords)
        {
            if (trimmedLine.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
                && (trimmedLine.Length == keyword.Length || !IsSimpleIdentifierPart(trimmedLine[keyword.Length])))
            {
                return true;
            }
        }

        return false;
    }

    private static void EmitObjCTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container)
    {
        if (StartsWithCharIgnoringLeadingWhitespace(preparedLine, '@') && preparedLine.IndexOf(':') >= 0)
        {
            foreach (Match match in ObjCInterfaceBaseRegex.Matches(preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "type_reference", context, lineNumber, container);
            }
        }

        if (preparedLine.IndexOf('<') >= 0 && preparedLine.IndexOf('>') >= 0)
        {
            foreach (Match match in ObjCProtocolListRegex.Matches(preparedLine))
                EmitCommaSeparatedNames(match.Groups["list"].Value, match.Groups["list"].Index, "objc", references, seen, fileId, context, lineNumber, container);
        }

        if (preparedLine.IndexOf('*') < 0)
            return;

        foreach (Match match in ObjCDeclTypeRegex.Matches(preparedLine))
        {
            var group = match.Groups["type"];
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "objc");
        }
    }

    private static void EmitHaskellTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("::", StringComparison.Ordinal) < 0)
            return;

        var match = HaskellSignatureRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var group = match.Groups["types"];
        ReferenceExtractor.AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            group.Value,
            group.Index,
            context,
            lineNumber,
            container,
            "haskell",
            BuildHaskellIgnoredTypeVariables(group.Value));
    }

    private static IReadOnlySet<string>? BuildHaskellIgnoredTypeVariables(string expression)
    {
        HashSet<string>? ignored = null;
        for (var cursor = 0; cursor < expression.Length; cursor++)
        {
            if (!IsSimpleIdentifierPart(expression[cursor]))
                continue;

            var start = cursor;
            while (cursor < expression.Length && IsSimpleIdentifierPart(expression[cursor]))
                cursor++;

            if (char.IsLower(expression[start]))
            {
                ignored ??= new HashSet<string>(StringComparer.Ordinal);
                ignored.Add(expression[start..cursor]);
            }

            cursor--;
        }

        return ignored;
    }

    private static void EmitElixirTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var hasImportMarker = StartsWithOrdinalKeywordIgnoringLeadingWhitespace(preparedLine, "alias")
            || StartsWithOrdinalKeywordIgnoringLeadingWhitespace(preparedLine, "import")
            || StartsWithOrdinalKeywordIgnoringLeadingWhitespace(preparedLine, "require")
            || StartsWithOrdinalKeywordIgnoringLeadingWhitespace(preparedLine, "use");
        if (hasImportMarker)
        {
            foreach (var match in EnumerateMatches(ElixirImportRegex, preparedLine))
                ReferenceExtractor.AddReference(references, seen, fileId, match, "type_reference", context, lineNumber, container);
        }

        var hasBehaviourMarker = StartsWithCharIgnoringLeadingWhitespace(preparedLine, '@')
            && (ContainsOrdinalKeyword(preparedLine, "behaviour")
                || ContainsOrdinalKeyword(preparedLine, "impl"));
        if (hasBehaviourMarker)
        {
            foreach (var match in EnumerateMatches(ElixirBehaviourRegex, preparedLine))
                ReferenceExtractor.AddReference(references, seen, fileId, match, "type_reference", context, lineNumber, container);
        }
    }

    private static bool IsIdentifierAt(string line, int index, string identifier)
    {
        if (index < 0 || index + identifier.Length > line.Length)
            return false;
        if (string.CompareOrdinal(line, index, identifier, 0, identifier.Length) != 0)
            return false;
        if (index > 0 && IsSimpleIdentifierPart(line[index - 1]))
            return false;

        var after = index + identifier.Length;
        return after >= line.Length || !IsSimpleIdentifierPart(line[after]);
    }

    private static bool IsSimpleIdentifierPart(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

}
