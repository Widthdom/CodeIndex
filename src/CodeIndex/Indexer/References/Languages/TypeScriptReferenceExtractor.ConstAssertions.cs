using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class TypeScriptReferenceExtractor
{
    private static void EmitAsTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("as", StringComparison.Ordinal) < 0)
            return;

        foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "as"))
        {
            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, asIndex + "as".Length);
            if (typeStart >= preparedLine.Length || TryConsumeKeywordAt(preparedLine, "const", typeStart))
                continue;

            var typeEnd = TypedLanguageReferenceExtractor.FindKeywordFollowingTypeExpressionEnd(preparedLine, typeStart, "typescript");
            if (typeEnd <= typeStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, typeEnd - typeStart),
                typeStart,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
        }
    }

    private static void EmitConstAssertionReferences(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<string> rawLines,
        int lineIndex,
        string preparedLine,
        string rawLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (preparedLine.IndexOf("as", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf("const", StringComparison.Ordinal) < 0)
        {
            return;
        }

        foreach (var asIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "as"))
        {
            var constIndex = SkipWhitespace(preparedLine, asIndex + "as".Length);
            if (!TryConsumeKeywordAt(preparedLine, "const", constIndex))
                continue;

            var rawAsIndex = rawLine.IndexOf(" as const", Math.Min(asIndex, rawLine.Length), StringComparison.Ordinal);
            if (rawAsIndex < 0)
                rawAsIndex = asIndex;
            else
                rawAsIndex++;
            var rawConstIndex = SkipWhitespace(rawLine, rawAsIndex + "as".Length);
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                "const",
                rawConstIndex,
                "const_assertion",
                context,
                lineNumber,
                resolveContainerForColumn(asIndex));

            EmitConstAssertionLiteralTypeReferences(
                preparedLines,
                rawLines,
                lineIndex,
                asIndex,
                rawAsIndex,
                references,
                seen,
                fileId,
                resolveContainerForColumn);
        }
    }

    private static void EmitConstAssertionLiteralTypeReferences(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<string> rawLines,
        int assertionLineIndex,
        int preparedAsIndex,
        int asIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!TryFindConstAssertionLiteralOpen(
                preparedLines,
                assertionLineIndex,
                preparedAsIndex,
                out var literalOpenLineIndex,
                out var literalOpenColumn))
        {
            return;
        }

        var insideBlockComment = false;
        for (var currentLineIndex = literalOpenLineIndex; currentLineIndex <= assertionLineIndex; currentLineIndex++)
        {
            var rawLine = rawLines[currentLineIndex];
            var scanStart = currentLineIndex == literalOpenLineIndex ? literalOpenColumn + 1 : 0;
            var scanEnd = currentLineIndex == assertionLineIndex ? Math.Min(asIndex, rawLine.Length) : rawLine.Length;
            if (scanStart >= scanEnd)
                continue;

            for (var index = scanStart; index < scanEnd; index++)
            {
                if (SkipConstAssertionComment(rawLine, scanEnd, ref index, ref insideBlockComment))
                    continue;

                if (rawLine[index] is '"' or '\'' or '`')
                {
                    var literalStart = index;
                    index = SkipQuotedLiteral(rawLine, index);
                    if (index <= literalStart + 1
                        || !HasStandaloneConstAssertionLiteralBoundaries(
                            rawLines,
                            literalOpenLineIndex,
                            literalOpenColumn,
                            assertionLineIndex,
                            asIndex,
                            currentLineIndex,
                            literalStart,
                            index + 1))
                    {
                        continue;
                    }

                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        rawLine.Substring(literalStart, index - literalStart + 1),
                        literalStart,
                        "type_reference",
                        rawLine.Trim(),
                        currentLineIndex + 1,
                        ResolveConstAssertionLiteralContainer(
                            currentLineIndex,
                            assertionLineIndex,
                            literalStart,
                            resolveContainerForColumn));
                    continue;
                }

                if (IsNumberLiteralStart(rawLine, index))
                {
                    var literalStart = index;
                    index = SkipNumberLiteral(rawLine, index);
                    if (!HasStandaloneConstAssertionLiteralBoundaries(
                            rawLines,
                            literalOpenLineIndex,
                            literalOpenColumn,
                            assertionLineIndex,
                            asIndex,
                            currentLineIndex,
                            literalStart,
                            index))
                    {
                        index--;
                        continue;
                    }

                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        rawLine.Substring(literalStart, index - literalStart),
                        literalStart,
                        "type_reference",
                        rawLine.Trim(),
                        currentLineIndex + 1,
                        ResolveConstAssertionLiteralContainer(
                            currentLineIndex,
                            assertionLineIndex,
                            literalStart,
                            resolveContainerForColumn));
                    index--;
                    continue;
                }

                if (!TryReadLiteralKeyword(rawLine, index, scanEnd, out var keyword))
                    continue;
                if (!HasStandaloneConstAssertionLiteralBoundaries(
                        rawLines,
                        literalOpenLineIndex,
                        literalOpenColumn,
                        assertionLineIndex,
                        asIndex,
                        currentLineIndex,
                        index,
                        index + keyword.Length))
                {
                    continue;
                }

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    keyword,
                    index,
                    "type_reference",
                    rawLine.Trim(),
                    currentLineIndex + 1,
                    ResolveConstAssertionLiteralContainer(
                        currentLineIndex,
                        assertionLineIndex,
                        index,
                        resolveContainerForColumn));
                index += keyword.Length - 1;
            }
        }
    }

    private static bool SkipConstAssertionComment(string line, int scanEnd, ref int index, ref bool insideBlockComment)
    {
        if (insideBlockComment)
        {
            var end = line.IndexOf("*/", index, Math.Max(0, scanEnd - index), StringComparison.Ordinal);
            if (end < 0)
            {
                index = scanEnd;
                return true;
            }

            index = end + 1;
            insideBlockComment = false;
            return true;
        }

        if (index + 1 >= scanEnd || line[index] != '/')
            return false;

        if (line[index + 1] == '/')
        {
            index = scanEnd;
            return true;
        }

        if (line[index + 1] != '*')
            return false;

        insideBlockComment = true;
        index++;
        return true;
    }

    private static bool HasStandaloneConstAssertionLiteralBoundaries(
        IReadOnlyList<string> preparedLines,
        int literalOpenLineIndex,
        int literalOpenColumn,
        int assertionLineIndex,
        int preparedAsIndex,
        int literalLineIndex,
        int literalStartColumn,
        int literalEndColumn)
    {
        var previous = FindPreviousNonWhitespace(
            preparedLines,
            literalOpenLineIndex,
            literalOpenColumn,
            literalLineIndex,
            literalStartColumn);
        var next = FindNextNonWhitespace(
            preparedLines,
            literalLineIndex,
            literalEndColumn,
            assertionLineIndex,
            preparedAsIndex);

        if (previous == ':' && HasQuestionBeforeLiteralValue(rawLines: preparedLines, literalLineIndex, literalStartColumn))
            return false;

        return previous is '[' or '{' or ':' or ','
               && next is ',' or ']' or '}';
    }

    private static bool HasQuestionBeforeLiteralValue(
        IReadOnlyList<string> rawLines,
        int literalLineIndex,
        int literalStartColumn)
    {
        var line = rawLines[literalLineIndex];
        for (var index = literalStartColumn - 1; index >= 0; index--)
        {
            if (line[index] == '?')
                return true;

            if (line[index] is ',' or '{' or '[')
                return false;
        }

        return false;
    }

    private static char? FindPreviousNonWhitespace(
        IReadOnlyList<string> lines,
        int minLineIndex,
        int minColumn,
        int lineIndex,
        int column)
    {
        for (var currentLineIndex = lineIndex; currentLineIndex >= minLineIndex; currentLineIndex--)
        {
            var line = lines[currentLineIndex];
            var index = currentLineIndex == lineIndex ? column - 1 : line.Length - 1;
            var stop = currentLineIndex == minLineIndex ? minColumn : 0;
            var lineCommentStart = IndexOfLineCommentOutsideString(line, stop, Math.Max(0, index - stop + 1));
            if (lineCommentStart >= 0)
                index = lineCommentStart - 1;
            for (; index >= stop; index--)
            {
                if (line[index] == '/' && index > stop && line[index - 1] == '*')
                {
                    var commentStart = line.LastIndexOf("/*", index - 1, index - stop, StringComparison.Ordinal);
                    if (commentStart >= 0)
                    {
                        index = commentStart;
                        continue;
                    }
                }

                if (!char.IsWhiteSpace(line[index]))
                    return line[index];
            }
        }

        return null;
    }

    private static int IndexOfLineCommentOutsideString(string line, int startIndex, int count)
    {
        var endIndex = Math.Min(line.Length, startIndex + count);
        char? quote = null;
        for (var index = startIndex; index + 1 < endIndex; index++)
        {
            if (quote is char activeQuote)
            {
                if (line[index] == '\\')
                {
                    index++;
                    continue;
                }

                if (line[index] == activeQuote)
                    quote = null;
                continue;
            }

            if (line[index] is '"' or '\'' or '`')
            {
                quote = line[index];
                continue;
            }

            if (line[index] == '/' && line[index + 1] == '/')
                return index;
        }

        return -1;
    }

    private static char? FindNextNonWhitespace(
        IReadOnlyList<string> lines,
        int lineIndex,
        int column,
        int maxLineIndex,
        int maxColumn)
    {
        for (var currentLineIndex = lineIndex; currentLineIndex <= maxLineIndex; currentLineIndex++)
        {
            var line = lines[currentLineIndex];
            var index = currentLineIndex == lineIndex ? column : 0;
            var stop = currentLineIndex == maxLineIndex ? Math.Min(maxColumn, line.Length) : line.Length;
            for (; index < stop; index++)
            {
                if (index + 1 < stop && line[index] == '/' && line[index + 1] == '*')
                {
                    var commentEnd = line.IndexOf("*/", index + 2, stop - index - 2, StringComparison.Ordinal);
                    if (commentEnd < 0)
                        return null;

                    index = commentEnd + 1;
                    continue;
                }

                if (index + 1 < stop && line[index] == '/' && line[index + 1] == '/')
                    return null;

                if (!char.IsWhiteSpace(line[index]))
                    return line[index];
            }
        }

        return null;
    }

    private static SymbolRecord? ResolveConstAssertionLiteralContainer(
        int literalLineIndex,
        int assertionLineIndex,
        int column,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        return literalLineIndex == assertionLineIndex ? resolveContainerForColumn(column) : null;
    }

    private static bool TryFindConstAssertionLiteralOpen(
        IReadOnlyList<string> preparedLines,
        int assertionLineIndex,
        int asIndex,
        out int openLineIndex,
        out int openColumn)
    {
        openLineIndex = -1;
        openColumn = -1;
        for (var lineIndex = assertionLineIndex; lineIndex >= 0; lineIndex--)
        {
            var line = preparedLines[lineIndex];
            var index = lineIndex == assertionLineIndex ? asIndex - 1 : line.Length - 1;
            for (; index >= 0; index--)
            {
                if (char.IsWhiteSpace(line[index]))
                    continue;

                if (line[index] is ']' or '}')
                {
                    var openChar = line[index] == ']' ? '[' : '{';
                    return TryFindMatchingOpenChar(
                        preparedLines,
                        lineIndex,
                        index,
                        openChar,
                        line[index],
                        out openLineIndex,
                        out openColumn);
                }

                return false;
            }
        }

        return false;
    }

    private static bool TryFindMatchingOpenChar(
        IReadOnlyList<string> lines,
        int closeLineIndex,
        int closeColumn,
        char openChar,
        char closeChar,
        out int openLineIndex,
        out int openColumn)
    {
        openLineIndex = -1;
        openColumn = -1;
        var depth = 0;
        for (var lineIndex = closeLineIndex; lineIndex >= 0; lineIndex--)
        {
            var line = lines[lineIndex];
            var index = lineIndex == closeLineIndex ? closeColumn : line.Length - 1;
            for (; index >= 0; index--)
            {
                if (line[index] == closeChar)
                {
                    depth++;
                    continue;
                }

                if (line[index] != openChar)
                    continue;

                depth--;
                if (depth == 0)
                {
                    openLineIndex = lineIndex;
                    openColumn = index;
                    return true;
                }
            }
        }

        return false;
    }

    private static int SkipQuotedLiteral(string text, int quoteIndex)
    {
        var quote = text[quoteIndex];
        for (var index = quoteIndex + 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
                continue;
            }

            if (text[index] == quote)
                return index;
        }

        return text.Length - 1;
    }

    private static bool IsNumberLiteralStart(string text, int index)
    {
        if (index >= text.Length)
            return false;

        var startsWithDigit = char.IsDigit(text[index]);
        var startsWithNegativeSign = text[index] == '-'
                                     && index + 1 < text.Length
                                     && char.IsDigit(text[index + 1]);
        if (!startsWithDigit && !startsWithNegativeSign)
            return false;

        return index == 0 || !IsTypeScriptIdentifierPart(text[index - 1]);
    }

    private static int SkipNumberLiteral(string text, int index)
    {
        if (index < text.Length && text[index] == '-')
            index++;

        if (index + 1 < text.Length
            && text[index] == '0'
            && text[index + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            var radixPrefix = text[index + 1];
            index += 2;
            while (index < text.Length && (IsRadixDigit(text[index], radixPrefix) || text[index] == '_'))
                index++;

            if (index < text.Length && text[index] == 'n')
                index++;

            return index;
        }

        while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_'))
            index++;

        if (index < text.Length && text[index] == '.')
        {
            index++;
            while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '_'))
                index++;
        }

        if (index < text.Length && text[index] is 'e' or 'E')
        {
            var exponentIndex = index + 1;
            if (exponentIndex < text.Length && text[exponentIndex] is '+' or '-')
                exponentIndex++;

            var digitStart = exponentIndex;
            while (exponentIndex < text.Length && (char.IsDigit(text[exponentIndex]) || text[exponentIndex] == '_'))
                exponentIndex++;

            if (exponentIndex > digitStart)
                index = exponentIndex;
        }

        if (index < text.Length && text[index] == 'n')
            index++;

        return index;
    }

    private static bool IsRadixDigit(char ch, char radixPrefix)
    {
        return radixPrefix switch
        {
            'x' or 'X' => char.IsAsciiHexDigit(ch),
            'b' or 'B' => ch is '0' or '1',
            'o' or 'O' => ch is >= '0' and <= '7',
            _ => false,
        };
    }

    private static bool TryReadLiteralKeyword(string text, int index, int endExclusive, out string keyword)
    {
        foreach (var candidate in LiteralKeywords)
        {
            if (index + candidate.Length > endExclusive
                || string.CompareOrdinal(text, index, candidate, 0, candidate.Length) != 0)
            {
                continue;
            }

            var beforeOk = index == 0 || !IsTypeScriptIdentifierPart(text[index - 1]);
            var after = index + candidate.Length;
            var afterOk = after >= text.Length || !IsTypeScriptIdentifierPart(text[after]);
            if (!beforeOk || !afterOk)
                continue;

            keyword = candidate;
            return true;
        }

        keyword = string.Empty;
        return false;
    }

    private static bool TryConsumeKeywordAt(string text, string keyword, int index)
    {
        if (index < 0 || index + keyword.Length > text.Length)
            return false;

        if (string.CompareOrdinal(text, index, keyword, 0, keyword.Length) != 0)
            return false;

        var beforeOk = index == 0 || !IsTypeScriptIdentifierPart(text[index - 1]);
        var after = index + keyword.Length;
        var afterOk = after >= text.Length || !IsTypeScriptIdentifierPart(text[after]);
        return beforeOk && afterOk;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

}
