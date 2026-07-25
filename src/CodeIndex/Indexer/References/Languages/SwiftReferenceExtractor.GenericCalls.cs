using CodeIndex.Models;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class SwiftReferenceExtractor
{
    private static void EmitCatchPatternTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var catchIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "catch"))
        {
            var patternStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, catchIndex + "catch".Length);
            if (patternStart >= preparedLine.Length
                || preparedLine[patternStart] == '{'
                || StartsWithSwiftWord(preparedLine, patternStart, "let")
                || StartsWithSwiftWord(preparedLine, patternStart, "var"))
            {
                continue;
            }

            if (StartsWithSwiftWord(preparedLine, patternStart, "is"))
            {
                patternStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, patternStart + "is".Length);
                if (patternStart >= preparedLine.Length || preparedLine[patternStart] == '{')
                    continue;
            }

            var typeEnd = FindSwiftCatchPatternTypeEnd(preparedLine, patternStart);
            if (typeEnd <= patternStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(patternStart, typeEnd - patternStart),
                patternStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(patternStart));
        }
    }

    private static int FindSwiftCatchPatternTypeEnd(string preparedLine, int patternStart)
    {
        for (var index = patternStart; index < preparedLine.Length; index++)
        {
            var ch = preparedLine[index];
            if (ch == '.'
                || ch == '{'
                || ch == ','
                || ch == '('
                || StartsWithSwiftWord(preparedLine, index, "where"))
            {
                return index;
            }
        }

        return preparedLine.Length;
    }

    private static void EmitGenericInvocationArgumentReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var index = 0; index < preparedLine.Length; index++)
        {
            if (!IsSwiftIdentifierStart(preparedLine[index]))
                continue;

            var nameStart = index;
            index++;
            while (index < preparedLine.Length && IsSwiftIdentifierPart(preparedLine[index]))
                index++;

            if (HasSwiftDeclarationKeywordBefore(preparedLine, nameStart)
                || index >= preparedLine.Length
                || preparedLine[index] != '<')
            {
                index--;
                continue;
            }

            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, index, '<', '>');
            if (closeAngle < 0)
            {
                index--;
                continue;
            }

            var afterGeneric = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeAngle + 1);
            if (afterGeneric >= preparedLine.Length || preparedLine[afterGeneric] is not ('(' or '{'))
            {
                index = closeAngle;
                continue;
            }

            TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
                preparedLine,
                index + 1,
                closeAngle,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            index = closeAngle;
        }
    }

    private static void EmitGenericStaticMemberTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var index = 0; index < preparedLine.Length; index++)
        {
            if (!LooksLikeSwiftTypeExpressionStart(preparedLine[index]))
                continue;

            var nameStart = index;
            index++;
            while (index < preparedLine.Length && IsSwiftIdentifierPart(preparedLine[index]))
                index++;

            if (HasSwiftDeclarationKeywordBefore(preparedLine, nameStart)
                || index >= preparedLine.Length
                || preparedLine[index] != '<')
            {
                index--;
                continue;
            }

            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, index, '<', '>');
            if (closeAngle < 0)
            {
                index--;
                continue;
            }

            var afterGeneric = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeAngle + 1);
            if (afterGeneric >= preparedLine.Length || preparedLine[afterGeneric] != '.')
            {
                index = closeAngle;
                continue;
            }

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(nameStart, closeAngle - nameStart + 1),
                nameStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(nameStart));
            index = closeAngle;
        }
    }

    private static bool HasSwiftDeclarationKeywordBefore(string preparedLine, int nameStart)
    {
        var previous = nameStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(preparedLine[previous]))
            previous--;
        if (previous < 0)
            return false;

        var wordEnd = previous + 1;
        while (previous >= 0 && IsSwiftIdentifierPart(preparedLine[previous]))
            previous--;
        var wordStart = previous + 1;
        if (wordStart >= wordEnd)
            return false;

        var word = preparedLine[wordStart..wordEnd];
        return word is "associatedtype" or "class" or "enum" or "extension" or "func" or "macro"
            or "protocol" or "struct" or "typealias";
    }

    private static void EmitMacroGenericArgumentReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (int hashIndex = 0; hashIndex < preparedLine.Length; hashIndex++)
        {
            if (preparedLine[hashIndex] != '#')
                continue;

            var nameStart = hashIndex + 1;
            if (nameStart >= preparedLine.Length || !IsSwiftIdentifierStart(preparedLine[nameStart]))
                continue;

            var nameEnd = nameStart + 1;
            while (nameEnd < preparedLine.Length && IsSwiftIdentifierPart(preparedLine[nameEnd]))
                nameEnd++;

            var openAngle = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, nameEnd);
            if (openAngle >= preparedLine.Length || preparedLine[openAngle] != '<')
                continue;

            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, openAngle, '<', '>');
            if (closeAngle < 0)
                continue;

            TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
                preparedLine,
                openAngle + 1,
                closeAngle,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            hashIndex = closeAngle;
        }
    }

    private static bool IsSwiftIdentifierStart(char ch)
        => ch == '_' || char.IsLetter(ch);

    private static bool IsSwiftIdentifierPart(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch);

    private static bool StartsWithSwiftWord(string text, int index, string word)
    {
        if (index < 0 || index + word.Length > text.Length)
            return false;
        if (!text.AsSpan(index, word.Length).SequenceEqual(word.AsSpan()))
            return false;

        var beforeOk = index == 0 || !IsSwiftIdentifierPart(text[index - 1]);
        var after = index + word.Length;
        var afterOk = after >= text.Length || !IsSwiftIdentifierPart(text[after]);
        return beforeOk && afterOk;
    }

    private static int FindSwiftKeyPathRootEnd(string preparedLine, int rootStart)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        for (int index = rootStart; index < preparedLine.Length; index++)
        {
            var ch = preparedLine[index];
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    else
                        return index;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    else
                        return index;
                    break;
                case '.':
                    if (angleDepth == 0
                        && parenDepth == 0
                        && squareDepth == 0
                        && index + 1 < preparedLine.Length
                        && (char.IsLower(preparedLine[index + 1]) || preparedLine[index + 1] == '_'))
                    {
                        return index;
                    }

                    break;
                case ',':
                case ';':
                case '{':
                case '}':
                    if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0)
                        return index;
                    break;
            }
        }

        return preparedLine.Length;
    }

}
