using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class TypeScriptReferenceExtractor
{
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
        var sameLine = preparedLines[openLineIndex];
        var sameLineStart = 0;
        while (sameLineStart < openColumn && char.IsWhiteSpace(sameLine[sameLineStart]))
            sameLineStart++;

        var sameLineEnd = openColumn;
        while (sameLineEnd > sameLineStart && char.IsWhiteSpace(sameLine[sameLineEnd - 1]))
            sameLineEnd--;

        if (sameLineEnd > sameLineStart)
        {
            var sameLinePrefix = sameLine.Substring(sameLineStart, sameLineEnd - sameLineStart);
            return IsImportBracePrefix(sameLinePrefix) || IsNamedExportBracePrefix(sameLinePrefix);
        }

        for (var lineIndex = openLineIndex - 1; lineIndex >= 0; lineIndex--)
        {
            var previousLineText = preparedLines[lineIndex];
            var previousLineStart = 0;
            while (previousLineStart < previousLineText.Length && char.IsWhiteSpace(previousLineText[previousLineStart]))
                previousLineStart++;

            var previousLineEnd = previousLineText.Length;
            while (previousLineEnd > previousLineStart && char.IsWhiteSpace(previousLineText[previousLineEnd - 1]))
                previousLineEnd--;

            if (previousLineEnd <= previousLineStart)
                continue;

            var previousLine = previousLineText.Substring(previousLineStart, previousLineEnd - previousLineStart);
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

        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;

        return end > 0 && text[end - 1] == ',';
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

    private static bool IsTypeScriptIdentifier(string text) =>
        IsTypeScriptIdentifier(text.AsSpan());

    private static bool IsTypeScriptIdentifier(ReadOnlySpan<char> text)
    {
        if (text.Length == 0 || !IsTypeScriptIdentifierStart(text[0]))
            return false;

        for (var index = 1; index < text.Length; index++)
        {
            if (!IsTypeScriptIdentifierPart(text[index]))
                return false;
        }

        return true;
    }

    private static bool IsTypeScriptIdentifierStart(char ch) =>
        ch == '_' || ch == '$' || char.IsLetter(ch);
}
