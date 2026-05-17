using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class TypeScriptReferenceExtractor
{
    private static readonly string[] DeclarationKeywords = ["const", "let", "var"];
    private static readonly string[] TypeOperatorKeywords = ["as", "satisfies", "instanceof"];

    public static void EmitTypePositionReferences(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string preparedLine,
        string rawLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        ReferenceExtractor.EmitTypeScriptTypePositionReferences(
            preparedLines,
            lineIndex,
            preparedLine,
            rawLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        EmitHeritageTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitCallableSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitFunctionPropertyTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitDecoratedMemberTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        TypedLanguageReferenceExtractor.EmitColonVariableTypeReferences(
            preparedLine,
            DeclarationKeywords,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        if (!IsImportExportAliasLine(preparedLines, lineIndex, preparedLine))
        {
            TypedLanguageReferenceExtractor.EmitKeywordFollowingTypeReferences(
                preparedLine,
                TypeOperatorKeywords,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    public static void EmitDeclarationTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        ReferenceExtractor.EmitTypeScriptDeclarationTypeReferences(
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitHeritageTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var trimmed = preparedLine.TrimStart();
        if (!(trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("abstract class ", StringComparison.Ordinal)
              || trimmed.StartsWith("export class ", StringComparison.Ordinal)
              || trimmed.StartsWith("export abstract class ", StringComparison.Ordinal)
              || trimmed.StartsWith("interface ", StringComparison.Ordinal)
              || trimmed.StartsWith("export interface ", StringComparison.Ordinal)))
        {
            return;
        }

        EmitHeritageKeyword(preparedLine, "extends", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitHeritageKeyword(preparedLine, "implements", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitHeritageKeyword(
        string preparedLine,
        string keyword,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
        {
            var listStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + keyword.Length);
            var listEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, listStart, stopAtComma: false);
            if (listEnd <= listStart)
                continue;

            TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
                preparedLine,
                listStart,
                listEnd,
                "typescript",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }
    }

    private static void EmitCallableSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(
            preparedLine,
            '(',
            SkipLeadingDecorators(preparedLine));
        if (openParen <= 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "typescript",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        var returnColon = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeParen + 1);
        if (returnColon >= preparedLine.Length || preparedLine[returnColon] != ':')
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, returnColon + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

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

    private static void EmitDecoratedMemberTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var memberStart = SkipLeadingDecorators(preparedLine);
        if (memberStart <= 0 || memberStart >= preparedLine.Length)
            return;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', memberStart);
        if (colonIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', memberStart);
        if (openParen >= 0 && openParen < colonIndex)
            return;

        var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', memberStart);
        if (equalsIndex >= 0 && equalsIndex < colonIndex)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

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

    private static void EmitFunctionPropertyTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':');
        if (colonIndex < 0)
            return;

        var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=');
        if (equalsIndex >= 0 && equalsIndex < colonIndex)
            return;

        var questionIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '?');
        if (questionIndex >= 0 && questionIndex != colonIndex - 1)
            return;

        var prefix = preparedLine.Substring(0, colonIndex).TrimEnd();
        if (prefix.Length == 0 || prefix.EndsWith(")", StringComparison.Ordinal))
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        if (typeStart >= preparedLine.Length)
            return;

        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        var container = resolveContainerForColumn(typeStart);
        TypedLanguageReferenceExtractor.TryEmitTypeScriptFunctionTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);
    }

    private static bool IsImportExportAliasLine(IReadOnlyList<string> preparedLines, int lineIndex, string preparedLine)
    {
        var trimmed = preparedLine.TrimStart();
        return IsImportDeclarationLine(trimmed)
               || IsNamedExportLine(trimmed)
               || IsExportStarAliasLine(trimmed)
               || IsInsideMultilineImportExportAlias(preparedLines, lineIndex, preparedLine);
    }

    private static bool IsImportDeclarationLine(string text)
    {
        const string importKeyword = "import";
        if (!text.StartsWith(importKeyword, StringComparison.Ordinal))
            return false;

        var index = importKeyword.Length;
        if (index >= text.Length || IsTypeScriptIdentifierPart(text[index]))
            return false;

        return char.IsWhiteSpace(text[index]) || text[index] is '{' or '*';
    }

    private static bool IsNamedExportLine(string text)
    {
        var index = 0;
        if (!TryConsumeKeyword(text, "export", ref index))
            return false;

        SkipWhitespace(text, ref index);
        if (index < text.Length && text[index] == '{')
            return true;

        if (!TryConsumeKeyword(text, "type", ref index))
            return false;

        SkipWhitespace(text, ref index);
        return index < text.Length && text[index] == '{';
    }

    private static bool IsExportStarAliasLine(string text)
    {
        var index = 0;
        if (!TryConsumeKeyword(text, "export", ref index))
            return false;

        SkipWhitespace(text, ref index);
        if (TryConsumeKeyword(text, "type", ref index))
            SkipWhitespace(text, ref index);

        if (index >= text.Length || text[index] != '*')
            return false;

        index++;
        SkipWhitespace(text, ref index);
        return TryConsumeKeyword(text, "as", ref index);
    }

    private static bool IsInsideMultilineImportExportAlias(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string preparedLine)
    {
        var asIndex = -1;
        foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "as"))
        {
            asIndex = keywordIndex;
            break;
        }

        return asIndex >= 0 && IsInsideImportExportBraceAt(preparedLines, lineIndex, asIndex);
    }

    private static bool IsInsideImportExportBraceAt(IReadOnlyList<string> preparedLines, int lineIndex, int column)
    {
        var unmatchedClosingBraces = 0;
        for (var currentLine = lineIndex; currentLine >= 0; currentLine--)
        {
            var line = preparedLines[currentLine];
            var startColumn = currentLine == lineIndex ? Math.Min(column, line.Length) - 1 : line.Length - 1;
            for (var index = startColumn; index >= 0; index--)
            {
                if (line[index] == '}')
                {
                    unmatchedClosingBraces++;
                    continue;
                }

                if (line[index] != '{')
                    continue;

                if (unmatchedClosingBraces > 0)
                {
                    unmatchedClosingBraces--;
                    continue;
                }

                return IsImportExportOpeningBrace(preparedLines, currentLine, index);
            }
        }

        return false;
    }

    private static int SkipLeadingDecorators(string line)
    {
        var index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index >= line.Length || line[index] != '@')
                return index;

            index++;
            while (index < line.Length && (IsTypeScriptIdentifierPart(line[index]) || line[index] == '.'))
                index++;

            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index < line.Length && line[index] == '(')
            {
                var closeParen = ReferenceExtractor.FindMatchingChar(line, index, '(', ')');
                if (closeParen < 0)
                    return index;

                index = closeParen + 1;
            }

            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;
        }

        return index;
    }

    private static bool IsImportExportOpeningBrace(IReadOnlyList<string> preparedLines, int openLineIndex, int openColumn)
    {
        var sameLinePrefix = preparedLines[openLineIndex].Substring(0, openColumn).Trim();
        if (sameLinePrefix.Length > 0)
            return IsImportBracePrefix(sameLinePrefix) || IsNamedExportBracePrefix(sameLinePrefix);

        for (var lineIndex = openLineIndex - 1; lineIndex >= 0; lineIndex--)
        {
            var previousLine = preparedLines[lineIndex].Trim();
            if (previousLine.Length == 0)
                continue;

            return IsImportBracePrefix(previousLine) || IsNamedExportBracePrefix(previousLine);
        }

        return false;
    }

    private static bool IsImportBracePrefix(string text)
    {
        if (text.IndexOf(';') >= 0 || ContainsTopLevelKeyword(text, "from"))
            return false;

        var index = 0;
        if (!TryConsumeKeyword(text, "import", ref index))
            return false;

        SkipWhitespace(text, ref index);
        if (index >= text.Length)
            return true;

        if (TryConsumeKeyword(text, "type", ref index))
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length)
                return true;
        }

        return text.TrimEnd().EndsWith(",", StringComparison.Ordinal);
    }

    private static bool ContainsTopLevelKeyword(string text, string keyword)
    {
        foreach (var _ in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(text, keyword))
            return true;

        return false;
    }

    private static bool IsNamedExportBracePrefix(string text)
    {
        var index = 0;
        if (!TryConsumeKeyword(text, "export", ref index))
            return false;

        SkipWhitespace(text, ref index);
        if (index >= text.Length)
            return true;

        if (!TryConsumeKeyword(text, "type", ref index))
            return false;

        SkipWhitespace(text, ref index);
        return index >= text.Length;
    }

    private static bool TryConsumeKeyword(string text, string keyword, ref int index)
    {
        if (index + keyword.Length > text.Length
            || string.CompareOrdinal(text, index, keyword, 0, keyword.Length) != 0)
        {
            return false;
        }

        var after = index + keyword.Length;
        if (after < text.Length && IsTypeScriptIdentifierPart(text[after]))
            return false;

        index = after;
        return true;
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool IsTypeScriptIdentifierPart(char ch) =>
        ch == '_' || ch == '$' || char.IsLetterOrDigit(ch);
}
