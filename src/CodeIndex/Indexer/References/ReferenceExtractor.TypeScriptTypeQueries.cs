using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool IsTypeScriptTypeQueryContext(
        IReadOnlyList<string> preparedLines,
        int lineIndex,
        string line,
        List<(int Start, int Length)> tokens,
        int keywordIndex)
    {
        for (int i = 0; i < keywordIndex; i++)
        {
            var token = line.Substring(tokens[i].Start, tokens[i].Length);
            if (TypeScriptTypeQueryDisqualifyingTokens.Contains(token))
                return false;

            if (TypeScriptTypeQueryContextTokens.Contains(token))
                return true;
        }

        if (keywordIndex == 0)
            return HasTypeScriptTypeQueryLeadingContext(preparedLines, lineIndex);

        var previousToken = line.Substring(tokens[keywordIndex - 1].Start, tokens[keywordIndex - 1].Length);
        return previousToken.EndsWith(':');
    }

    private static bool HasTypeScriptTypeQueryLeadingContext(IReadOnlyList<string> preparedLines, int lineIndex)
    {
        for (int previousIndex = lineIndex - 1; previousIndex >= 0; previousIndex--)
        {
            var previousLine = preparedLines[previousIndex];
            if (string.IsNullOrWhiteSpace(previousLine))
                continue;

            if (IsTypeScriptTypeQueryLineContext(previousLine))
                return true;

            if (!IsTypeScriptTypeQueryContinuationLine(previousLine))
                return false;
        }

        return false;
    }

    private static bool IsTypeScriptTypeQueryLineContext(string line)
    {
        var tokens = GetTopLevelTokenSpans(line);
        foreach (var token in tokens)
        {
            var text = line.Substring(token.Start, token.Length);
            if (TypeScriptTypeQueryDisqualifyingTokens.Contains(text))
                return false;
            if (TypeScriptTypeQueryContextTokens.Contains(text))
                return true;
        }

        return false;
    }

    private static bool IsTypeScriptTypeQueryContinuationLine(string line)
    {
        var trimmed = line.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        return trimmed[^1] is '<' or '(' or '[' or ',' or '|' or '&' or ':';
    }

    private static bool TryExtractTypeScriptTypeQueryTarget(
        string line,
        int startIndex,
        out int targetStart,
        out int targetLength,
        out string? literalTarget)
    {
        targetStart = 0;
        targetLength = 0;
        literalTarget = null;

        var cursor = startIndex;
        while (cursor < line.Length)
        {
            cursor = SkipWhitespace(line, cursor);
            if (cursor >= line.Length)
                return false;

            if (TryConsumeTypeScriptTypeQueryWrapper(line, cursor, "typeof", out cursor))
                continue;

            if (TryConsumeTypeScriptImportTypeWrapper(line, cursor, out cursor, out var importModuleStart, out var importModuleLength))
            {
                if (cursor >= line.Length || line[cursor] != '.')
                {
                    targetStart = importModuleStart;
                    targetLength = importModuleLength;
                    literalTarget = line.Substring(importModuleStart, importModuleLength);
                    return targetLength > 0;
                }

                continue;
            }

            if (line[cursor] == '(' || line[cursor] == '[')
            {
                cursor++;
                continue;
            }

            break;
        }

        cursor = SkipWhitespace(line, cursor);
        while (cursor < line.Length && line[cursor] == '.')
        {
            cursor++;
            cursor = SkipWhitespace(line, cursor);
        }

        if (cursor >= line.Length || !IsJavaIdentifierStart(line[cursor]))
            return false;

        var end = cursor + 1;
        while (end < line.Length && (IsJavaIdentifierPart(line[end]) || line[end] == '.'))
            end++;

        targetStart = cursor;
        targetLength = end - cursor;
        return targetLength > 0;
    }

    private static bool TryConsumeTypeScriptTypeQueryWrapper(
        string line,
        int cursor,
        string keyword,
        out int nextCursor)
    {
        nextCursor = cursor;
        if (cursor + keyword.Length > line.Length
            || !line.AsSpan(cursor, keyword.Length).Equals(keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var nextIndex = cursor + keyword.Length;
        if (nextIndex < line.Length && (char.IsLetterOrDigit(line[nextIndex]) || line[nextIndex] == '_'))
            return false;

        nextCursor = nextIndex;
        return true;
    }

    private static bool TryConsumeTypeScriptImportTypeWrapper(
        string line,
        int cursor,
        out int nextCursor,
        out int moduleStart,
        out int moduleLength)
    {
        nextCursor = cursor;
        moduleStart = -1;
        moduleLength = 0;
        if (cursor + "import".Length > line.Length
            || !line.AsSpan(cursor, "import".Length).Equals("import", StringComparison.Ordinal))
        {
            return false;
        }

        var nextIndex = cursor + "import".Length;
        if (nextIndex < line.Length && (char.IsLetterOrDigit(line[nextIndex]) || line[nextIndex] == '_'))
            return false;

        nextIndex = SkipWhitespace(line, nextIndex);
        if (nextIndex >= line.Length || line[nextIndex] != '(')
            return false;

        var moduleQuoteIndex = SkipWhitespace(line, nextIndex + 1);
        if (moduleQuoteIndex >= line.Length || line[moduleQuoteIndex] is not '\'' and not '"')
            return false;

        var moduleLiteralStart = moduleQuoteIndex + 1;
        var moduleLiteralEnd = SkipTypeScriptStringLiteral(line, moduleQuoteIndex) - 1;
        if (moduleLiteralEnd < moduleLiteralStart)
            return false;

        var closeIndex = SkipBalanced(line, nextIndex, '(', ')');
        if (closeIndex <= nextIndex)
            return false;

        moduleStart = moduleLiteralStart;
        moduleLength = moduleLiteralEnd - moduleLiteralStart;
        nextCursor = closeIndex;
        return true;
    }
}
