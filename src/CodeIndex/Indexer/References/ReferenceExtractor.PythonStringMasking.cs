namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static string MaskPythonSingleLineFStrings(string line)
    {
        if (line.IndexOf('f') < 0 && line.IndexOf('F') < 0)
            return line;

        char[]? masked = null;
        for (var i = 0; i < line.Length; i++)
        {
            if (!TryOpenPythonSingleLineString(line, i, out var prefixLength, out var quoteChar, out var isRaw, out var isFString))
                continue;

            if (!isFString)
            {
                i += prefixLength;
                continue;
            }

            var chars = masked ??= line.ToCharArray();
            var quoteStart = i + prefixLength;
            var openingLength = prefixLength + 1;
            ReplaceWithSpaces(chars, i, openingLength);
            i += openingLength;

            var inExpression = false;
            var expressionDepth = 0;
            while (i < line.Length)
            {
                if (!inExpression)
                {
                    if (!isRaw && line[i] == '\\' && i + 1 < line.Length)
                    {
                        ReplaceWithSpaces(chars, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '{' && i + 1 < line.Length && line[i + 1] == '{')
                    {
                        ReplaceWithSpaces(chars, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '}' && i + 1 < line.Length && line[i + 1] == '}')
                    {
                        ReplaceWithSpaces(chars, i, 2);
                        i += 2;
                        continue;
                    }

                    if (line[i] == '{')
                    {
                        chars[i] = ' ';
                        inExpression = true;
                        expressionDepth = 1;
                        i++;
                        continue;
                    }

                    if (line[i] == quoteChar)
                    {
                        chars[i] = ' ';
                        i++;
                        break;
                    }

                    chars[i] = ' ';
                    i++;
                    continue;
                }

                if (line[i] == '{')
                {
                    expressionDepth++;
                    i++;
                    continue;
                }

                if (line[i] == '}')
                {
                    expressionDepth--;
                    chars[i] = ' ';
                    i++;
                    if (expressionDepth == 0)
                        inExpression = false;
                    continue;
                }

                if (line[i] == '\'' || line[i] == '"')
                {
                    var nestedQuote = line[i];
                    i++;
                    while (i < line.Length)
                    {
                        if (line[i] == '\\' && i + 1 < line.Length)
                        {
                            i += 2;
                            continue;
                        }

                        if (line[i] == nestedQuote)
                        {
                            i++;
                            break;
                        }

                        i++;
                    }

                    // Leave nested string contents in place; the generic string regex
                    // masks them later while the outer f-string wrapper is already gone.
                    // ネスト文字列は内容を残す。外側 f-string の殻を先に除去しておき、
                    // 内側の generic string regex で後からまとめてマスクする。
                    continue;
                }

                if (line[i] == '#')
                {
                    chars[i] = ' ';
                    i++;
                    continue;
                }

                i++;
            }

            i = Math.Max(i - 1, quoteStart);
        }

        return masked is null ? line : new string(masked);
    }

    private static string[] MaskPythonFStrings(IReadOnlyList<string> lines)
    {
        if (lines is string[] lineArray && !MayContainPythonFString(lines))
            return lineArray;

        var result = new string[lines.Count];
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            result[lineIndex] = lines[lineIndex];

        for (var lineIndex = 0; lineIndex < result.Length; lineIndex++)
        {
            var line = result[lineIndex];
            if (line.IndexOf('f') < 0 && line.IndexOf('F') < 0)
                continue;

            for (var column = 0; column < line.Length; column++)
            {
                if (!TryOpenPythonString(line, column, out var prefixLength, out var quoteChar, out var isRaw, out var isFString, out var isTripleQuoted))
                    continue;

                if (!isFString)
                {
                    column += prefixLength;
                    continue;
                }

                if (!isTripleQuoted)
                {
                    result[lineIndex] = MaskPythonSingleLineFStrings(line);
                    break;
                }

                MaskPythonTripleQuotedFString(result, lineIndex, column, prefixLength, quoteChar, isRaw, out var endLineIndex, out var endColumn);
                lineIndex = endLineIndex;
                line = result[lineIndex];
                column = endColumn;
            }
        }

        return result;
    }

    private static bool MayContainPythonFString(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.IndexOf('f') >= 0 || line.IndexOf('F') >= 0)
                return true;
        }

        return false;
    }

    private static void MaskPythonTripleQuotedFString(
        string[] lines,
        int startLineIndex,
        int startColumn,
        int prefixLength,
        char quoteChar,
        bool isRaw,
        out int endLineIndex,
        out int endColumn)
    {
        var lineIndex = startLineIndex;
        var column = startColumn;
        var inExpression = false;
        var inExpressionString = false;
        var expressionDepth = 0;
        var expressionStringQuote = '\0';
        var expressionStringTripleQuoted = false;

        endLineIndex = startLineIndex;
        endColumn = startColumn;

        while (lineIndex < lines.Length)
        {
            var line = lines[lineIndex];
            var chars = line.ToCharArray();
            if (lineIndex == startLineIndex)
            {
                ReplaceWithSpaces(chars, startColumn, prefixLength + 3);
                column = startColumn + prefixLength + 3;
            }
            else
            {
                column = 0;
            }

            while (column < line.Length)
            {
                if (!inExpression)
                {
                    if (!isRaw && line[column] == '\\' && column + 1 < line.Length)
                    {
                        ReplaceWithSpaces(chars, column, 2);
                        column += 2;
                        continue;
                    }

                    if (line[column] == '{' && column + 1 < line.Length && line[column + 1] == '{')
                    {
                        ReplaceWithSpaces(chars, column, 2);
                        column += 2;
                        continue;
                    }

                    if (line[column] == '}' && column + 1 < line.Length && line[column + 1] == '}')
                    {
                        ReplaceWithSpaces(chars, column, 2);
                        column += 2;
                        continue;
                    }

                    if (line[column] == '{')
                    {
                        chars[column++] = ' ';
                        inExpression = true;
                        expressionDepth = 1;
                        continue;
                    }

                    if (column + 2 < line.Length
                        && line[column] == quoteChar
                        && line[column + 1] == quoteChar
                        && line[column + 2] == quoteChar)
                    {
                        ReplaceWithSpaces(chars, column, 3);
                        lines[lineIndex] = new string(chars);
                        endLineIndex = lineIndex;
                        endColumn = column + 2;
                        return;
                    }

                    chars[column++] = ' ';
                    continue;
                }

                if (inExpressionString)
                {
                    if (line[column] == '\\' && column + 1 < line.Length)
                    {
                        column += 2;
                        continue;
                    }

                    if (expressionStringTripleQuoted)
                    {
                        if (column + 2 < line.Length
                            && line[column] == expressionStringQuote
                            && line[column + 1] == expressionStringQuote
                            && line[column + 2] == expressionStringQuote)
                        {
                            column += 3;
                            inExpressionString = false;
                            continue;
                        }

                        column++;
                        continue;
                    }

                    if (line[column] == expressionStringQuote)
                    {
                        column++;
                        inExpressionString = false;
                        continue;
                    }

                    column++;
                    continue;
                }

                if (line[column] == '\'' || line[column] == '"')
                {
                    expressionStringQuote = line[column];
                    expressionStringTripleQuoted = column + 2 < line.Length
                        && line[column + 1] == expressionStringQuote
                        && line[column + 2] == expressionStringQuote;
                    column += expressionStringTripleQuoted ? 3 : 1;
                    inExpressionString = true;
                    continue;
                }

                if (line[column] == '{')
                {
                    expressionDepth++;
                    column++;
                    continue;
                }

                if (line[column] == '}')
                {
                    expressionDepth--;
                    chars[column++] = ' ';
                    if (expressionDepth == 0)
                        inExpression = false;
                    continue;
                }

                column++;
            }

            lines[lineIndex] = new string(chars);
            lineIndex++;
        }

        endLineIndex = Math.Max(startLineIndex, lines.Length - 1);
        endColumn = 0;
    }

    private static void ReplaceWithSpaces(char[] buffer, int start, int length)
    {
        for (var i = start; i < start + length && i < buffer.Length; i++)
            buffer[i] = ' ';
    }

    private static bool TryOpenPythonSingleLineString(
        string line,
        int startIndex,
        out int prefixLength,
        out char quoteChar,
        out bool isRaw,
        out bool isFString)
    {
        prefixLength = 0;
        quoteChar = '\0';
        isRaw = false;
        isFString = false;

        if (startIndex < 0 || startIndex >= line.Length)
            return false;

        if (startIndex > 0 && IsIdentifierChar(line[startIndex - 1]))
            return false;

        var p = startIndex;
        var prefixChars = 0;
        while (p < line.Length && prefixChars < 2 && IsPythonStringPrefixChar(line[p]))
        {
            if (line[p] is 'r' or 'R')
                isRaw = true;
            if (line[p] is 'f' or 'F')
                isFString = true;
            p++;
            prefixChars++;
        }

        if (p >= line.Length || (line[p] != '\'' && line[p] != '"'))
            return false;
        if (p + 2 < line.Length && line[p] == line[p + 1] && line[p] == line[p + 2])
            return false;

        prefixLength = p - startIndex;
        quoteChar = line[p];
        return true;
    }

    private static bool TryOpenPythonString(
        string line,
        int startIndex,
        out int prefixLength,
        out char quoteChar,
        out bool isRaw,
        out bool isFString,
        out bool isTripleQuoted)
    {
        isTripleQuoted = false;
        if (!TryOpenPythonSingleOrTripleString(line, startIndex, out prefixLength, out quoteChar, out isRaw, out isFString, out isTripleQuoted))
            return false;
        return true;
    }

    private static bool TryOpenPythonSingleOrTripleString(
        string line,
        int startIndex,
        out int prefixLength,
        out char quoteChar,
        out bool isRaw,
        out bool isFString,
        out bool isTripleQuoted)
    {
        prefixLength = 0;
        quoteChar = '\0';
        isRaw = false;
        isFString = false;
        isTripleQuoted = false;

        if (startIndex < 0 || startIndex >= line.Length)
            return false;

        if (startIndex > 0 && IsIdentifierChar(line[startIndex - 1]))
            return false;

        var p = startIndex;
        var prefixChars = 0;
        while (p < line.Length && prefixChars < 2 && IsPythonStringPrefixChar(line[p]))
        {
            if (line[p] is 'r' or 'R')
                isRaw = true;
            if (line[p] is 'f' or 'F')
                isFString = true;
            p++;
            prefixChars++;
        }

        if (p >= line.Length || (line[p] != '\'' && line[p] != '"'))
            return false;

        prefixLength = p - startIndex;
        quoteChar = line[p];
        isTripleQuoted = p + 2 < line.Length && line[p + 1] == quoteChar && line[p + 2] == quoteChar;
        return true;
    }
}
