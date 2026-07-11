namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static bool TryReadJavaScriptTypeScriptStaticImportModule(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out string moduleName,
        out int moduleLineIndex,
        out int moduleStartColumn,
        out int endLineIndex,
        out int endColumn,
        out string signature)
    {
        moduleName = string.Empty;
        moduleLineIndex = -1;
        moduleStartColumn = -1;
        endLineIndex = -1;
        endColumn = -1;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        var importColumn = SkipWhitespace(startLine, startColumn);
        if (!IsJavaScriptTypeScriptKeywordAt(startLine, importColumn, "import"))
            return false;

        var afterImportColumn = importColumn + "import".Length;
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterImportColumn,
                Math.Min(sanitizedLines.Length, startLineIndex + 16),
                out var nextLineIndex,
                out var nextColumn))
        {
            return false;
        }

        var nextChar = sanitizedLines[nextLineIndex][nextColumn];
        if (nextChar is '(' or '.')
            return false;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);
        var quoteLineIndex = -1;
        var quoteColumn = -1;

        if (nextChar is '\'' or '"')
        {
            quoteLineIndex = nextLineIndex;
            quoteColumn = nextColumn;
        }
        else
        {
            if (!TryFindJavaScriptTypeScriptStaticImportFromKeyword(
                    sanitizedLines,
                    startLineIndex,
                    afterImportColumn,
                    scanEndExclusive,
                    out var fromLineIndex,
                    out var fromColumn)
                || !TryFindNextJavaScriptTypeScriptNonWhitespace(
                    sanitizedLines,
                    fromLineIndex,
                    fromColumn + "from".Length,
                    scanEndExclusive,
                    out quoteLineIndex,
                    out quoteColumn))
            {
                return false;
            }
        }

        var sanitizedQuote = sanitizedLines[quoteLineIndex][quoteColumn];
        if (sanitizedQuote is not '\'' and not '"')
            return false;

        if (!TryReadJavaScriptTypeScriptQuotedModuleName(
                rawLines,
                quoteLineIndex,
                quoteColumn,
                sanitizedQuote,
                out moduleName,
                out moduleStartColumn,
                out var moduleEndColumn))
        {
            return false;
        }

        if (!TryFindJavaScriptTypeScriptStaticImportEnd(
                sanitizedLines,
                quoteLineIndex,
                moduleEndColumn + 1,
                scanEndExclusive,
                out endLineIndex,
                out endColumn))
        {
            return false;
        }

        moduleLineIndex = quoteLineIndex;
        signature = BuildJavaScriptTypeScriptStatementSignature(rawLines, startLineIndex, importColumn, endLineIndex, endColumn);
        return moduleName.Length > 0;
    }

    private static bool TryReadJavaScriptTypeScriptRequireModule(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int afterRequireColumn,
        out string moduleName,
        out int moduleLineIndex,
        out int moduleStartColumn,
        out int endLineIndex,
        out string signature,
        bool allowTrailingArguments = false)
    {
        moduleName = string.Empty;
        moduleLineIndex = -1;
        moduleStartColumn = -1;
        endLineIndex = -1;
        signature = string.Empty;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterRequireColumn,
                scanEndExclusive,
                out var openParenLineIndex,
                out var openParenColumn)
            || sanitizedLines[openParenLineIndex][openParenColumn] != '(')
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                openParenLineIndex,
                openParenColumn + 1,
                scanEndExclusive,
                out moduleLineIndex,
                out var moduleQuoteColumn))
        {
            return false;
        }

        var sanitizedQuote = sanitizedLines[moduleLineIndex][moduleQuoteColumn];
        if (sanitizedQuote is not '\'' and not '"' and not '`')
            return false;

        if (!TryReadJavaScriptTypeScriptQuotedModuleName(
                rawLines,
                moduleLineIndex,
                moduleQuoteColumn,
                sanitizedQuote,
                out moduleName,
                out moduleStartColumn,
                out var moduleEndColumn))
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                moduleLineIndex,
                moduleEndColumn + 1,
                scanEndExclusive,
                out var afterSpecifierLineIndex,
                out var afterSpecifierColumn))
        {
            return false;
        }

        int closeParenLineIndex;
        int closeParenColumn;
        var afterSpecifierChar = sanitizedLines[afterSpecifierLineIndex][afterSpecifierColumn];
        if (afterSpecifierChar == ')')
        {
            closeParenLineIndex = afterSpecifierLineIndex;
            closeParenColumn = afterSpecifierColumn;
        }
        else if (allowTrailingArguments && afterSpecifierChar == ',')
        {
            if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                    sanitizedLines,
                    openParenLineIndex,
                    openParenColumn,
                    scanEndExclusive,
                    out closeParenLineIndex,
                    out closeParenColumn)
                || closeParenLineIndex < afterSpecifierLineIndex
                || (closeParenLineIndex == afterSpecifierLineIndex && closeParenColumn < afterSpecifierColumn))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        endLineIndex = closeParenLineIndex;
        signature = BuildJavaScriptTypeScriptDynamicImportSignature(
            rawLines,
            startLineIndex,
            closeParenLineIndex,
            closeParenColumn);
        return moduleName.Length > 0;
    }

    private static bool TryFindJavaScriptTypeScriptStaticImportFromKeyword(
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        int endLineExclusive,
        out int fromLineIndex,
        out int fromColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var lineIndex = startLineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            while (column < line.Length)
            {
                var ch = line[column];
                if (parenDepth == 0
                    && bracketDepth == 0
                    && braceDepth == 0
                    && IsJavaScriptTypeScriptKeywordAt(line, column, "from"))
                {
                    fromLineIndex = lineIndex;
                    fromColumn = column;
                    return true;
                }

                if (ch == ';' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    break;

                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (bracketDepth > 0)
                            bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                }

                column++;
            }
        }

        fromLineIndex = -1;
        fromColumn = -1;
        return false;
    }

    private static bool TryReadJavaScriptTypeScriptQuotedModuleName(
        string[] rawLines,
        int quoteLineIndex,
        int quoteColumn,
        char quoteChar,
        out string moduleName,
        out int moduleStartColumn,
        out int moduleEndColumn)
    {
        moduleName = string.Empty;
        moduleStartColumn = -1;
        moduleEndColumn = -1;

        var rawModuleLine = rawLines[quoteLineIndex];
        if (quoteColumn >= rawModuleLine.Length || rawModuleLine[quoteColumn] != quoteChar)
            return false;

        moduleStartColumn = quoteColumn + 1;
        moduleEndColumn = moduleStartColumn;
        while (moduleEndColumn < rawModuleLine.Length)
        {
            if (rawModuleLine[moduleEndColumn] == '\\' && moduleEndColumn + 1 < rawModuleLine.Length)
            {
                moduleEndColumn += 2;
                continue;
            }

            if (rawModuleLine[moduleEndColumn] == quoteChar)
                break;

            moduleEndColumn++;
        }

        if (moduleEndColumn <= moduleStartColumn
            || moduleEndColumn >= rawModuleLine.Length
            || rawModuleLine[moduleEndColumn] != quoteChar)
        {
            return false;
        }

        moduleName = rawModuleLine.Substring(moduleStartColumn, moduleEndColumn - moduleStartColumn);
        if (quoteChar == '`'
            && ContainsJavaScriptTypeScriptTemplateInterpolation(rawModuleLine, moduleStartColumn, moduleEndColumn))
        {
            moduleName = string.Empty;
            return false;
        }

        return true;
    }

    private static bool ContainsJavaScriptTypeScriptTemplateInterpolation(string rawLine, int startColumn, int endColumn)
    {
        for (var column = Math.Max(0, startColumn); column + 1 < endColumn && column + 1 < rawLine.Length; column++)
        {
            if (rawLine[column] != '$' || rawLine[column + 1] != '{')
                continue;

            var backslashCount = 0;
            for (var probe = column - 1; probe >= startColumn && rawLine[probe] == '\\'; probe--)
                backslashCount++;

            if (backslashCount % 2 == 0)
                return true;
        }

        return false;
    }

    private static bool TryFindJavaScriptTypeScriptStaticImportEnd(
        string[] sanitizedLines,
        int startScanLineIndex,
        int startScanColumn,
        int endLineExclusive,
        out int endLineIndex,
        out int endColumn)
    {
        endLineIndex = startScanLineIndex;
        endColumn = Math.Max(0, startScanColumn - 1);

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var sawAttributeKeyword = false;
        var sawAttributeBrace = false;

        for (var lineIndex = startScanLineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = sanitizedLines[lineIndex];
            var column = lineIndex == startScanLineIndex ? Math.Max(0, startScanColumn) : 0;
            var lastNonWhitespaceColumn = -1;

            while (column < line.Length)
            {
                var ch = line[column];
                if (!char.IsWhiteSpace(ch))
                    lastNonWhitespaceColumn = column;

                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                {
                    if (ch == ';')
                    {
                        endLineIndex = lineIndex;
                        endColumn = column;
                        return true;
                    }

                    if (IsJavaScriptTypeScriptKeywordAt(line, column, "with")
                        || IsJavaScriptTypeScriptKeywordAt(line, column, "assert"))
                    {
                        sawAttributeKeyword = true;
                    }
                }

                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (bracketDepth > 0)
                            bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        if (sawAttributeKeyword)
                            sawAttributeBrace = true;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        if (sawAttributeKeyword && sawAttributeBrace && braceDepth == 0)
                        {
                            endLineIndex = lineIndex;
                            endColumn = column;
                        }
                        break;
                }

                column++;
            }

            if (lastNonWhitespaceColumn >= 0)
            {
                endLineIndex = lineIndex;
                endColumn = lastNonWhitespaceColumn;
            }

            if (!sawAttributeKeyword || (sawAttributeBrace && braceDepth == 0))
                return true;
        }

        return endLineIndex >= startScanLineIndex && endColumn >= 0;
    }

    private static bool TryReadJavaScriptTypeScriptDynamicImportModule(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int afterImportColumn,
        out string moduleName,
        out int moduleLineIndex,
        out int moduleStartColumn,
        out string signature)
    {
        moduleName = string.Empty;
        moduleLineIndex = -1;
        moduleStartColumn = -1;
        signature = string.Empty;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterImportColumn,
                scanEndExclusive,
                out var openParenLineIndex,
                out var openParenColumn)
            || sanitizedLines[openParenLineIndex][openParenColumn] != '(')
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                openParenLineIndex,
                openParenColumn + 1,
                scanEndExclusive,
                out moduleLineIndex,
                out var moduleQuoteColumn))
        {
            return false;
        }

        var sanitizedQuote = sanitizedLines[moduleLineIndex][moduleQuoteColumn];
        if (sanitizedQuote is not '\'' and not '"' and not '`')
            return false;

        if (!TryReadJavaScriptTypeScriptQuotedModuleName(
                rawLines,
                moduleLineIndex,
                moduleQuoteColumn,
                sanitizedQuote,
                out moduleName,
                out moduleStartColumn,
                out var moduleEndColumn))
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                moduleLineIndex,
                moduleEndColumn + 1,
                scanEndExclusive,
                out var afterSpecifierLineIndex,
                out var afterSpecifierColumn))
        {
            return false;
        }

        int closeParenLineIndex;
        int closeParenColumn;
        var afterSpecifierChar = sanitizedLines[afterSpecifierLineIndex][afterSpecifierColumn];
        if (afterSpecifierChar == ')')
        {
            closeParenLineIndex = afterSpecifierLineIndex;
            closeParenColumn = afterSpecifierColumn;
        }
        else if (afterSpecifierChar == ',')
        {
            if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                    sanitizedLines,
                    openParenLineIndex,
                    openParenColumn,
                    scanEndExclusive,
                    out closeParenLineIndex,
                    out closeParenColumn)
                || closeParenLineIndex < afterSpecifierLineIndex
                || (closeParenLineIndex == afterSpecifierLineIndex && closeParenColumn < afterSpecifierColumn))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        signature = BuildJavaScriptTypeScriptDynamicImportSignature(
            rawLines,
            startLineIndex,
            closeParenLineIndex,
            closeParenColumn);
        return true;
    }

    private static bool TryFindJavaScriptTypeScriptDynamicImportCloseParen(
        string[] sanitizedLines,
        int openParenLineIndex,
        int openParenColumn,
        int endLineExclusive,
        out int closeParenLineIndex,
        out int closeParenColumn)
    {
        var parenDepth = 0;
        for (var lineIndex = openParenLineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = sanitizedLines[lineIndex];
            var column = lineIndex == openParenLineIndex ? openParenColumn : 0;
            while (column < line.Length)
            {
                var ch = line[column];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    if (parenDepth == 0)
                        break;

                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        closeParenLineIndex = lineIndex;
                        closeParenColumn = column;
                        return true;
                    }
                }

                column++;
            }
        }

        closeParenLineIndex = -1;
        closeParenColumn = -1;
        return false;
    }

    private static bool TryFindNextJavaScriptTypeScriptNonWhitespace(
        string[] lines,
        int startLineIndex,
        int startColumn,
        int endLineExclusive,
        out int lineIndex,
        out int column)
    {
        for (lineIndex = startLineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = lines[lineIndex];
            column = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            while (column < line.Length && char.IsWhiteSpace(line[column]))
                column++;

            if (column < line.Length)
                return true;
        }

        lineIndex = -1;
        column = -1;
        return false;
    }
}
