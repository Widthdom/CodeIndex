using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static void EmitFortranCallReferences(string preparedLine, Action<string, int> addCallLikeReference)
    {
        if (!StartsWithKeywordIgnoringLeadingWhitespace(preparedLine, "call"))
            return;

        foreach (Match match in FortranCallRegex.Matches(preparedLine))
            addCallLikeReference(match.Groups["name"].Value, match.Groups["name"].Index);
    }

    private static void EmitPascalCallReferences(string preparedLine, Action<string, int> addCallLikeReference, IReadOnlySet<string>? definitionNames)
    {
        if (preparedLine.IndexOf(';') < 0)
            return;

        var match = PascalBareCallRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var name = match.Groups["name"].Value;
        if (definitionNames?.Contains(name) == true)
            return;

        addCallLikeReference(name, match.Groups["name"].Index);
    }

    private static void EmitObjCMessageReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf('[') >= 0)
        {
            foreach (Match match in ObjCMessageRegex.Matches(preparedLine))
            {
                var receiver = match.Groups["receiver"];
                var selector = match.Groups["name"];
                if (char.IsUpper(receiver.Value[0]) && selector.Value is "alloc" or "new")
                {
                    ReferenceExtractor.AddReference(references, seen, fileId, receiver.Value, receiver.Index, "instantiate", context, lineNumber, resolveContainerForColumn(receiver.Index));
                }

                addCallLikeReference(selector.Value, selector.Index);
            }
        }

        if (preparedLine.IndexOf("@selector", StringComparison.Ordinal) >= 0
            && preparedLine.IndexOf('(') >= 0)
        {
            foreach (Match match in ObjCSelectorRegex.Matches(preparedLine))
                addCallLikeReference(match.Groups["name"].Value.TrimEnd(':'), match.Groups["name"].Index);
        }
    }

    private static void EmitHaskellSpaceCallReferences(string preparedLine, Action<string, int> addCallLikeReference, IReadOnlySet<string>? definitionNames)
    {
        if (!ContainsWhitespace(preparedLine))
            return;

        string? definitionName = null;
        var scanStart = 0;
        var scanText = preparedLine;
        if (preparedLine.IndexOf('=') >= 0)
        {
            var definitionMatch = HaskellDefinitionRegex.Match(preparedLine);
            if (definitionMatch.Success)
            {
                definitionName = definitionMatch.Groups["name"].Value;
                var equalsIndex = preparedLine.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    scanStart = equalsIndex + 1;
                    scanText = preparedLine[scanStart..];
                }
            }
        }

        foreach (Match match in HaskellSpaceCallRegex.Matches(scanText))
        {
            var name = match.Groups["name"].Value;
            if (definitionNames?.Contains(name) == true || string.Equals(name, definitionName, StringComparison.Ordinal))
                continue;
            addCallLikeReference(name, scanStart + match.Groups["name"].Index);
        }
    }

    private static void EmitElixirParenlessCallReferences(string preparedLine, Action<string, int> addCallLikeReference, IReadOnlySet<string>? definitionNames)
    {
        if (!ContainsWhitespace(preparedLine))
            return;

        foreach (Match match in ElixirParenlessCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (definitionNames?.Contains(name) == true)
                continue;
            addCallLikeReference(name, match.Groups["name"].Index);
        }
    }

    private static void EmitSmalltalkMessageReferences(string preparedLine, Action<string, int> addCallLikeReference, IReadOnlySet<string>? definitionNames)
    {
        if (!ContainsWhitespace(preparedLine))
            return;

        var isDefinitionLine = preparedLine.IndexOf(">>", StringComparison.Ordinal) >= 0
            && SmalltalkMethodDefinitionRegex.IsMatch(preparedLine);
        var hasClassDeclarationLiteralMarker = preparedLine.IndexOf('#') >= 0
            && (preparedLine.IndexOf("subclass:", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("Class", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("Object", StringComparison.Ordinal) >= 0);
        if (isDefinitionLine || (hasClassDeclarationLiteralMarker && SmalltalkClassDeclarationRegex.IsMatch(preparedLine)))
            return;

        var consumedUntil = 0;
        foreach (Match match in SmalltalkMessageSendRegex.Matches(preparedLine))
        {
            if (match.Index < consumedUntil)
                continue;

            var selectorGroup = match.Groups["selector"];
            var name = ReadSmalltalkSelector(preparedLine, selectorGroup.Index, out var selectorEndIndex);
            consumedUntil = Math.Max(consumedUntil, selectorEndIndex);
            if (definitionNames?.Contains(name) == true)
                continue;
            addCallLikeReference(name, selectorGroup.Index);
        }
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
                return true;
        }

        return false;
    }

    private static string ReadSmalltalkSelector(string line, int selectorIndex, out int endIndex)
    {
        if (!TryReadSmalltalkSelectorPart(line, selectorIndex, out var firstPart, out var cursor))
        {
            endIndex = selectorIndex;
            return string.Empty;
        }

        if (!firstPart.EndsWith(':'))
        {
            endIndex = cursor;
            return firstPart;
        }

        var selector = firstPart;
        while (true)
        {
            var argumentStart = SkipWhitespace(line, cursor);
            if (argumentStart >= line.Length || !IsIdentifierStart(line[argumentStart]))
                break;

            var argumentEnd = argumentStart + 1;
            while (argumentEnd < line.Length && IsSimpleIdentifierPart(line[argumentEnd]))
                argumentEnd++;

            var nextSelectorStart = SkipWhitespace(line, argumentEnd);
            if (!TryReadSmalltalkSelectorPart(line, nextSelectorStart, out var nextPart, out var nextEnd)
                || !nextPart.EndsWith(':'))
            {
                break;
            }

            selector += nextPart;
            cursor = nextEnd;
        }

        endIndex = cursor;
        return selector;
    }

    private static bool TryReadSmalltalkSelectorPart(string line, int start, out string part, out int end)
    {
        part = string.Empty;
        end = start;
        if (start >= line.Length || !IsIdentifierStart(line[start]))
            return false;

        end = start + 1;
        while (end < line.Length && IsSimpleIdentifierPart(line[end]))
            end++;
        if (end < line.Length && line[end] == ':')
            end++;

        part = line[start..end];
        return true;
    }

    private static void EmitCommaSeparatedNames(
        string list,
        int listStart,
        string language,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var leading = ReferenceExtractor.CountLeadingWhitespace(list, segmentStart, segmentLength);
            var trimmedLength = segmentLength - leading;
            while (trimmedLength > 0 && char.IsWhiteSpace(list[segmentStart + leading + trimmedLength - 1]))
                trimmedLength--;
            if (trimmedLength == 0)
                continue;
            var expressionStart = segmentStart + leading;
            var raw = list.Substring(expressionStart, trimmedLength);
            if (language == "vb")
            {
                var equalsIndex = list.IndexOf('=', segmentStart, segmentLength);
                if (equalsIndex >= 0)
                {
                    var rhsStart = equalsIndex + 1;
                    var rhsLength = segmentStart + segmentLength - rhsStart;
                    var rhsLeading = ReferenceExtractor.CountLeadingWhitespace(list, rhsStart, rhsLength);
                    expressionStart = rhsStart + rhsLeading;
                    var rhsTrimmedLength = rhsLength - rhsLeading;
                    while (rhsTrimmedLength > 0 && char.IsWhiteSpace(list[expressionStart + rhsTrimmedLength - 1]))
                        rhsTrimmedLength--;
                    if (rhsTrimmedLength == 0)
                        continue;

                    raw = list.Substring(expressionStart, rhsTrimmedLength);
                }
            }

            var name = GetLastWhitespaceSeparatedToken(raw);
            var offset = list.IndexOf(name, expressionStart, StringComparison.Ordinal);
            if (offset < 0)
                offset = expressionStart;
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, name, listStart + offset, context, lineNumber, container, language);
        }
    }

    private static string GetLastWhitespaceSeparatedToken(string value)
    {
        var end = value.Length;
        while (end > 0 && (value[end - 1] == ' ' || value[end - 1] == '\t'))
            end--;
        var start = end;
        while (start > 0 && value[start - 1] != ' ' && value[start - 1] != '\t')
            start--;

        return start == 0 && end == value.Length ? value : value[start..end];
    }

}
