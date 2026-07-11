namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindJavaScriptBraceRange(string[] lines, int startIndex, string? lang, int startColumn = 0)
    {
        var depth = 0;
        var opened = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var pendingArrowBody = false;
        var arrowExpressionActive = false;
        var arrowExpressionParenDepth = 0;
        var arrowExpressionBracketDepth = 0;
        var arrowExpressionBraceDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        int? bodyStartLine = null;
        var lexState = new JavaScriptLexState();

        for (int i = startIndex; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var effectiveStartColumn = startColumn;
            if (i == startIndex
                && startColumn > 0
                && TryParseJavaScriptTypeScriptMethodHeader(lexedLine.SanitizedLine, startColumn, lang, out var methodHeader))
            {
                effectiveStartColumn = methodHeader.BodyStartColumn;
            }

            var scanLine = i == startIndex && effectiveStartColumn > 0 && effectiveStartColumn < lexedLine.SanitizedLine.Length
                ? lexedLine.SanitizedLine[effectiveStartColumn..]
                : i == startIndex && effectiveStartColumn >= lexedLine.SanitizedLine.Length
                    ? string.Empty
                    : lexedLine.SanitizedLine;

            if (arrowExpressionActive
                && arrowExpressionBraceDepth == 0
                && arrowExpressionParenDepth == 0
                && arrowExpressionBracketDepth == 0
                && !StartsJavaScriptTypeScriptExpressionContinuation(scanLine))
            {
                return (i, bodyStartLine ?? startIndex + 1, i);
            }

            for (int column = 0; column < scanLine.Length; column++)
            {
                var ch = scanLine[column];
                if (!opened
                    && !arrowExpressionActive
                    && !functionHeaderState.Active
                    && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < scanLine.Length && IsJavaScriptTypeScriptIdentifierPart(scanLine[tokenEnd]))
                        tokenEnd++;

                    if (scanLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                if (!opened && !arrowExpressionActive)
                {
                    var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                        ref functionHeaderState,
                        scanLine,
                        column,
                        lang ?? "javascript",
                        out var functionHeaderAdvanceColumns);
                    if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                    {
                        column += functionHeaderAdvanceColumns;
                        continue;
                    }
                }

                if (!opened && !arrowExpressionActive && i == startIndex && ch == '=' && column + 1 < scanLine.Length && scanLine[column + 1] == '>')
                {
                    pendingArrowBody = true;
                    column++;
                    continue;
                }

                if (pendingArrowBody)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;

                    bodyStartLine ??= i + 1;
                    if (ch == '{')
                        pendingArrowBody = false;
                    else
                    {
                        arrowExpressionActive = true;
                        pendingArrowBody = false;
                    }
                }

                if (arrowExpressionActive)
                {
                    if (ch == '(')
                    {
                        arrowExpressionParenDepth++;
                        continue;
                    }

                    if (ch == ')' && arrowExpressionParenDepth > 0)
                    {
                        arrowExpressionParenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        arrowExpressionBracketDepth++;
                        continue;
                    }

                    if (ch == ']' && arrowExpressionBracketDepth > 0)
                    {
                        arrowExpressionBracketDepth--;
                        continue;
                    }

                    if (ch == '{')
                    {
                        arrowExpressionBraceDepth++;
                        continue;
                    }

                    if (ch == '}' && arrowExpressionBraceDepth > 0)
                    {
                        arrowExpressionBraceDepth--;
                        continue;
                    }

                    if (ch == ';'
                        && arrowExpressionParenDepth == 0
                        && arrowExpressionBracketDepth == 0
                        && arrowExpressionBraceDepth == 0)
                    {
                        return (i + 1, bodyStartLine ?? startIndex + 1, i + 1);
                    }

                    continue;
                }

                if (!opened)
                {
                    if (ch == '(')
                    {
                        parenDepth++;
                        continue;
                    }

                    if (ch == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        bracketDepth++;
                        continue;
                    }

                    if (ch == ']' && bracketDepth > 0)
                    {
                        bracketDepth--;
                        continue;
                    }

                    if (ch == '<')
                    {
                        if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                            angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }
                }

                if (ch == '{')
                {
                    if (!opened && (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0))
                        continue;

                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                        return (i + 1, bodyStartLine, i + 1);
                }
            }

            if (!opened
                && !arrowExpressionActive
                && !functionHeaderState.Active
                && parenDepth == 0
                && bracketDepth == 0
                && angleDepth == 0
                && FindJavaScriptTypeScriptTrimmedEndExclusive(scanLine, scanLine.Length) is var trimmedEnd
                && trimmedEnd > 0
                && scanLine[trimmedEnd - 1] == ';')
                return (startIndex + 1, null, null);
        }

        if (arrowExpressionActive)
            return (lines.Length, bodyStartLine ?? startIndex + 1, lines.Length);

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    private static int FindJavaScriptBodyOpenBraceIndex(string[] lines, int startIndex, int bodyStartIndex, string? lang, int startColumn = 0)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var lexState = new JavaScriptLexState();

        for (int i = startIndex; i <= bodyStartIndex; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var initialColumn = i == startIndex ? Math.Max(0, startColumn) : 0;
            for (int column = initialColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                        tokenEnd++;

                    if (sanitizedLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                    ref functionHeaderState,
                    sanitizedLine,
                    column,
                    lang ?? "javascript",
                    out var functionHeaderAdvanceColumns);
                if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                {
                    column += functionHeaderAdvanceColumns;
                    continue;
                }

                if (!char.IsWhiteSpace(ch))
                {
                    if (ch == '(')
                    {
                        parenDepth++;
                        continue;
                    }

                    if (ch == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        bracketDepth++;
                        continue;
                    }

                    if (ch == ']' && bracketDepth > 0)
                    {
                        bracketDepth--;
                        continue;
                    }

                    if (ch == '<')
                    {
                        if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                            angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }
                }

                if (ch != '{')
                    continue;

                if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                    continue;

                return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptSameLineBodyEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        if (!TryParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out var methodHeader))
            return -1;

        return FindJavaScriptSameLineBraceBodyEndColumn(sanitizedLine, methodHeader.BodyStartColumn);
    }

    // Same-line body end finder for class-field arrow functions. The scanner already knows the
    // sanitized body-open column from the arrow capture, so we walk braces from that column
    // without re-parsing the header (which the method-header parser would reject).
    // クラスフィールドのアロー関数向けの同一行 body 終了列探索。スキャナが arrow capture の段階で
    // sanitized 上の body 開始列を把握しているので、ヘッダを再パースせずそこから brace を辿る。
    private static int FindJavaScriptSameLineArrowBodyEndColumn(string line, int bodyStartColumn)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        return FindJavaScriptSameLineBraceBodyEndColumn(sanitizedLine, bodyStartColumn);
    }

    private static int FindJavaScriptSameLineBraceBodyEndColumn(string sanitizedLine, int bodyStartColumn)
    {
        var depth = 0;
        var opened = false;

        for (int column = Math.Max(0, bodyStartColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptSymbolStartColumn(string line, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return 0;

        var startColumn = line.IndexOf(signature, StringComparison.Ordinal);
        return startColumn >= 0 ? startColumn : 0;
    }

    private static int FindJavaScriptTypeScriptSameLineBraceEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        var bodyStartColumn = FindJavaScriptTypeScriptBodyOpenBraceColumn(sanitizedLine, startColumn, lang);
        if (bodyStartColumn < 0)
            return -1;

        var depth = 0;
        var opened = false;

        for (int column = bodyStartColumn; column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptBodyOpenBraceColumn(string sanitizedLine, int startColumn, string? lang)
    {
        if (TryParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out var methodHeader))
            return methodHeader.BodyStartColumn;

        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
            {
                var tokenStart = column;
                var tokenEnd = column + 1;
                while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                    tokenEnd++;

                if (sanitizedLine[tokenStart..tokenEnd] == "function")
                {
                    BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                    column = tokenEnd - 1;
                    continue;
                }
            }

            var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                ref functionHeaderState,
                sanitizedLine,
                column,
                lang ?? "javascript",
                out var functionHeaderAdvanceColumns);
            if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
            {
                column += functionHeaderAdvanceColumns;
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
                if (ch == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    continue;
                }

                if (ch == '<')
                {
                    if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                        angleDepth++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0)
                {
                    angleDepth--;
                    continue;
                }
            }

            if (ch != '{')
                continue;

            if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                continue;

            return column;
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptSameLineStatementEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (char.IsWhiteSpace(ch))
                continue;

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
                case '<':
                    if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                        angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                        return column;
                    break;
            }
        }

        return -1;
    }
}
