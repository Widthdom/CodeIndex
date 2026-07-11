namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static bool StartsJavaScriptTypeScriptFunctionAssignmentValue(string rhs)
        => StartsJavaScriptTypeScriptFunctionAssignmentValue(rhs, 0);

    private static bool StartsJavaScriptTypeScriptFunctionAssignmentValue(string rhs, int startColumn)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, Math.Max(0, startColumn));
        while (index < rhs.Length)
        {
            if (IsJavaScriptTypeScriptKeywordAt(rhs, index, "function")
                || StartsJavaScriptTypeScriptAsyncFunctionAssignmentValue(rhs, index)
                || StartsJavaScriptTypeScriptGenericArrowAssignmentValue(rhs, index)
                || StartsJavaScriptTypeScriptArrowAssignmentValue(rhs, index))
            {
                return true;
            }

            if (rhs[index] != '(')
                return false;

            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + 1);
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptAsyncFunctionAssignmentValue(string rhs, int startColumn)
    {
        if (!IsJavaScriptTypeScriptKeywordAt(rhs, startColumn, "async"))
            return false;

        var functionColumn = SkipJavaScriptTypeScriptWhitespace(rhs, startColumn + "async".Length);
        return IsJavaScriptTypeScriptKeywordAt(rhs, functionColumn, "function");
    }

    private static bool StartsJavaScriptTypeScriptGenericArrowAssignmentValue(string rhs, int startColumn)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, startColumn);
        if (IsJavaScriptTypeScriptKeywordAt(rhs, index, "async"))
            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + "async".Length);

        if (index >= rhs.Length || rhs[index] != '<')
            return false;

        var genericEnd = FindJavaScriptTypeScriptBalancedGenericListEnd(rhs, index);
        if (genericEnd < 0)
            return false;

        var remainderIndex = SkipJavaScriptTypeScriptWhitespace(rhs, genericEnd + 1);
        if (remainderIndex >= rhs.Length)
            return false;

        if (rhs[remainderIndex] == '(')
        {
            var parameterListEnd = FindJavaScriptTypeScriptBalancedDelimiterEnd(rhs, remainderIndex, '(', ')');
            if (parameterListEnd < 0)
                return false;

            remainderIndex = SkipJavaScriptTypeScriptWhitespace(rhs, parameterListEnd + 1);
        }
        else
        {
            var parameterNameLength = ReadJavaScriptTypeScriptIdentifierLength(rhs, remainderIndex);
            if (parameterNameLength <= 0)
                return false;

            remainderIndex = SkipJavaScriptTypeScriptWhitespace(rhs, remainderIndex + parameterNameLength);
        }

        return remainderIndex + 1 < rhs.Length
            && rhs[remainderIndex] == '='
            && rhs[remainderIndex + 1] == '>';
    }

    private static bool StartsJavaScriptTypeScriptArrowAssignmentValue(string rhs, int startColumn)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, startColumn);
        if (IsJavaScriptTypeScriptKeywordAt(rhs, index, "async"))
            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + "async".Length);

        if (index >= rhs.Length)
            return false;

        if (rhs[index] == '(')
        {
            var parameterListEnd = rhs.IndexOf(')', index + 1);
            if (parameterListEnd < 0)
                return false;

            var arrowColumn = SkipJavaScriptTypeScriptWhitespace(rhs, parameterListEnd + 1);
            return arrowColumn + 1 < rhs.Length
                && rhs[arrowColumn] == '='
                && rhs[arrowColumn + 1] == '>';
        }

        if (!IsJavaScriptTypeScriptIdentifierStart(rhs[index]))
            return false;

        while (index < rhs.Length && IsJavaScriptTypeScriptIdentifierPart(rhs[index]))
            index++;

        index = SkipJavaScriptTypeScriptWhitespace(rhs, index);
        return index + 1 < rhs.Length
            && rhs[index] == '='
            && rhs[index + 1] == '>';
    }

    private static int SkipJavaScriptTypeScriptWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;

        return index;
    }

    private static string TrimJavaScriptTypeScriptStart(string text, int startIndex = 0)
    {
        var trimmed = text.AsSpan(startIndex).TrimStart();
        return startIndex == 0 && trimmed.Length == text.Length ? text : trimmed.ToString();
    }

    private static bool StartsJavaScriptTypeScriptArrowFunctionAssignmentValue(string rhs)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, 0);
        while (index < rhs.Length)
        {
            if (StartsJavaScriptTypeScriptArrowFunctionAssignmentValue(rhs, index))
            {
                return true;
            }

            if (rhs[index] != '(')
                return false;

            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + 1);
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptArrowFunctionAssignmentValue(string rhs, int startColumn)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, Math.Max(0, startColumn));
        return StartsJavaScriptTypeScriptGenericArrowAssignmentValue(rhs, index)
            || StartsJavaScriptTypeScriptArrowAssignmentValue(rhs, index);
    }

    private static bool StartsJavaScriptTypeScriptLambdaAssignmentValue(string rhs)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, 0);
        while (index < rhs.Length)
        {
            if (StartsJavaScriptTypeScriptArrowFunctionAssignmentValue(rhs, index)
                || StartsJavaScriptTypeScriptAnonymousFunctionAssignmentValue(rhs, index))
            {
                return true;
            }

            if (rhs[index] != '(')
                return false;

            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + 1);
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptClassAssignmentValue(string rhs)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, 0);
        while (index < rhs.Length)
        {
            if (IsJavaScriptTypeScriptKeywordAt(rhs, index, "class"))
                return true;

            if (rhs[index] != '(')
                return false;

            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + 1);
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptAnonymousFunctionAssignmentValue(string rhs)
        => StartsJavaScriptTypeScriptAnonymousFunctionAssignmentValue(rhs, 0);

    private static bool StartsJavaScriptTypeScriptAnonymousFunctionAssignmentValue(string rhs, int startColumn)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, Math.Max(0, startColumn));
        if (IsJavaScriptTypeScriptKeywordAt(rhs, index, "async"))
            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + "async".Length);

        if (!IsJavaScriptTypeScriptKeywordAt(rhs, index, "function"))
            return false;

        index = SkipJavaScriptTypeScriptWhitespace(rhs, index + "function".Length);
        if (index < rhs.Length && rhs[index] == '*')
            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + 1);

        return index < rhs.Length && rhs[index] == '(';
    }

    private static bool StartsJavaScriptTypeScriptPotentialGenericArrowAssignmentValue(string rhs)
    {
        var index = SkipJavaScriptTypeScriptWhitespace(rhs, 0);
        if (IsJavaScriptTypeScriptKeywordAt(rhs, index, "async"))
            index = SkipJavaScriptTypeScriptWhitespace(rhs, index + "async".Length);

        return index < rhs.Length && rhs[index] == '<';
    }

    private static string CollectJavaScriptTypeScriptAssignedRhsHeader(string[] sanitizedLines, int startLineIndex, int startColumn)
    {
        var builder = new System.Text.StringBuilder(EstimateJavaScriptTypeScriptStatementCapacity(sanitizedLines, startLineIndex));
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var genericDepth = 0;
        var sawGenericStart = false;

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex
                ? Math.Max(0, startColumn)
                : 0;
            if (column >= sanitizedLine.Length)
                continue;

            if (builder.Length > 0)
                builder.Append(' ');

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                builder.Append(ch);

                if (!sawGenericStart)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;

                    if (ch == '<')
                    {
                        sawGenericStart = true;
                        genericDepth = 1;
                    }

                    continue;
                }

                switch (ch)
                {
                    case '<':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            genericDepth++;
                        break;
                    case '>':
                        if (parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0
                            && genericDepth > 0
                            && (column == 0 || sanitizedLine[column - 1] != '='))
                        {
                            genericDepth--;
                        }
                        break;
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
                        if (genericDepth == 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return builder.ToString().Trim();

                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                    case '=':
                        if (column + 1 < sanitizedLine.Length
                            && sanitizedLine[column + 1] == '>'
                            && genericDepth == 0
                            && parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0)
                        {
                            builder.Append('>');
                            column++;
                            return builder.ToString().Trim();
                        }
                        break;
                }
            }

            if (sawGenericStart
                && genericDepth == 0
                && parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool StartsJavaScriptTypeScriptGenericArrowAssignmentValue(string rhs)
        => StartsJavaScriptTypeScriptGenericArrowAssignmentValue(rhs, 0);

    private static bool TryCollectJavaScriptTypeScriptAssignedRhs(
        string[] rawLines,
        string[] sanitizedLines,
        int assignmentLineIndex,
        int assignmentStartColumn,
        int sameLineRhsColumn,
        string lang,
        out string rhs,
        out int rhsStartLineIndex,
        out int rhsStartColumn,
        out int rhsEndLineIndex,
        out int rhsEndColumn,
        out string signature)
    {
        rhs = string.Empty;
        rhsStartLineIndex = assignmentLineIndex;
        rhsStartColumn = sameLineRhsColumn;
        rhsEndLineIndex = assignmentLineIndex;
        rhsEndColumn = -1;
        signature = string.Empty;

        var builderCapacity = EstimateJavaScriptTypeScriptStatementCapacity(sanitizedLines, assignmentLineIndex);
        var rhsBuilder = new System.Text.StringBuilder(builderCapacity);
        var signatureBuilder = new System.Text.StringBuilder(builderCapacity);
        var pendingWrapperParenClose = false;

        for (int lineIndex = assignmentLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var column = lineIndex == assignmentLineIndex
                ? Math.Max(0, sameLineRhsColumn)
                : 0;

            if (!TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(sanitizedLines, ref lineIndex, ref column))
                continue;

            var sanitizedLine = sanitizedLines[lineIndex];
            while (sanitizedLines[lineIndex][column] == '('
                && HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(sanitizedLines[lineIndex], column))
            {
                column++;
                pendingWrapperParenClose = true;
                if (!TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(sanitizedLines, ref lineIndex, ref column))
                    return false;

                sanitizedLine = sanitizedLines[lineIndex];
            }

            if (pendingWrapperParenClose && column < sanitizedLine.Length && sanitizedLine[column] == ')')
            {
                column++;
                pendingWrapperParenClose = false;
            }

            var statementEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, column, lang);
            var sliceEndExclusive = statementEndColumn >= column
                ? statementEndColumn + 1
                : sanitizedLine.Length;

            var rhsStartSliceColumn = Math.Min(column, sanitizedLine.Length);
            var statementSliceEndColumn = Math.Min(sliceEndExclusive, sanitizedLine.Length);
            var rhsSlice = rhsStartSliceColumn < statementSliceEndColumn
                ? sanitizedLine[rhsStartSliceColumn..statementSliceEndColumn].TrimEnd()
                : string.Empty;
            if (rhsSlice.Length > 0)
            {
                if (rhsBuilder.Length == 0)
                {
                    rhsStartLineIndex = lineIndex;
                    rhsStartColumn = rhsStartSliceColumn;
                }

                if (rhsBuilder.Length > 0)
                    rhsBuilder.Append(' ');
                rhsBuilder.Append(rhsSlice);
            }

            var signatureSlice = lineIndex == assignmentLineIndex
                ? rawLines[lineIndex][Math.Min(assignmentStartColumn, rawLines[lineIndex].Length)..Math.Min(rawLines[lineIndex].Length, statementSliceEndColumn)].Trim()
                : rawLines[lineIndex].Trim();
            if (signatureSlice.Length > 0)
            {
                if (signatureBuilder.Length > 0)
                    signatureBuilder.Append(' ');
                signatureBuilder.Append(signatureSlice);
            }

            if (statementEndColumn >= column)
            {
                rhsEndLineIndex = lineIndex;
                rhsEndColumn = statementEndColumn;
                rhs = rhsBuilder.ToString().Trim();
                signature = signatureBuilder.ToString().Trim();
                return true;
            }
        }

        if (rhsBuilder.Length > 0)
        {
            rhs = rhsBuilder.ToString().Trim();
            signature = signatureBuilder.ToString().Trim();
            rhsEndLineIndex = Math.Max(assignmentLineIndex, sanitizedLines.Length - 1);
            rhsEndColumn = sanitizedLines[rhsEndLineIndex].Length - 1;
            return true;
        }

        rhs = string.Empty;
        signature = string.Empty;
        return false;
    }

    private static bool TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(string[] sanitizedLines, ref int lineIndex, ref int column)
    {
        while (lineIndex < sanitizedLines.Length)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                column++;

            if (column < sanitizedLine.Length)
                return true;

            lineIndex++;
            column = 0;
        }

        return false;
    }

    private static bool TryFindJavaScriptTypeScriptAssignedRhsStart(
        string[] sanitizedLines,
        int assignmentLineIndex,
        int sameLineRhsColumn,
        out int startLineIndex,
        out int startColumn)
    {
        for (int lineIndex = assignmentLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == assignmentLineIndex
                ? Math.Max(0, sameLineRhsColumn)
                : 0;

            while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                column++;

            if (column >= sanitizedLine.Length)
                continue;

            if (sanitizedLine[column] == '('
                && HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(sanitizedLine, column))
            {
                continue;
            }

            if (sanitizedLine[column] == ')')
            {
                var remainder = sanitizedLine[column..].Trim();
                if (remainder.Length == 0 || remainder == ")" || remainder == ");")
                    continue;
            }

            startLineIndex = lineIndex;
            startColumn = column;
            return true;
        }

        startLineIndex = assignmentLineIndex;
        startColumn = sameLineRhsColumn;
        return false;
    }

    private static bool TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
        string[] rawLines,
        int startLineIndex,
        int startColumn,
        string? lang,
        out int openBraceLineIndex,
        out int openBraceColumn)
    {
        openBraceLineIndex = -1;
        openBraceColumn = -1;

        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var awaitingFunctionBody = false;
        var awaitingArrowBody = false;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var lexState = new JavaScriptLexState();

        for (int lineIndex = startLineIndex; lineIndex < rawLines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(rawLines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var column = lineIndex == startLineIndex
                ? Math.Max(0, startColumn)
                : 0;

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                var wasFunctionHeaderActive = functionHeaderState.Active;

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
                if (wasFunctionHeaderActive && !functionHeaderState.Active)
                    awaitingFunctionBody = true;

                if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                {
                    column += functionHeaderAdvanceColumns;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                    continue;

                if (awaitingFunctionBody)
                {
                    if (ch == '{')
                    {
                        openBraceLineIndex = lineIndex;
                        openBraceColumn = column;
                        return true;
                    }

                    return false;
                }

                if (awaitingArrowBody)
                {
                    if (ch == '{')
                    {
                        openBraceLineIndex = lineIndex;
                        openBraceColumn = column;
                        return true;
                    }

                    return false;
                }

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

                if (lang == "typescript" && ch == '<' && parenDepth == 0 && bracketDepth == 0)
                {
                    angleDepth++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0 && (column == 0 || sanitizedLine[column - 1] != '='))
                {
                    angleDepth--;
                    continue;
                }

                if (ch == '='
                    && column + 1 < sanitizedLine.Length
                    && sanitizedLine[column + 1] == '>'
                    && parenDepth == 0
                    && bracketDepth == 0
                    && angleDepth == 0)
                {
                    awaitingArrowBody = true;
                    column++;
                }
            }
        }

        return false;
    }

    private static bool HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(string sanitizedLine, int startColumn)
    {
        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (char.IsWhiteSpace(ch) || ch == '(')
                continue;

            return false;
        }

        return true;
    }

    private static int FindJavaScriptTypeScriptBalancedGenericListEnd(string text, int startIndex)
    {
        if (startIndex < 0
            || startIndex >= text.Length
            || text[startIndex] != '<')
        {
            return -1;
        }

        var depth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (int index = startIndex; index < text.Length; index++)
        {
            var ch = text[index];
            switch (ch)
            {
                case '<':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        depth++;
                    break;
                case '>':
                    if (parenDepth == 0
                        && bracketDepth == 0
                        && braceDepth == 0
                        && depth > 0
                        && (index == 0 || text[index - 1] != '='))
                    {
                        depth--;
                        if (depth == 0)
                            return index;
                    }
                    break;
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
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptBalancedDelimiterEnd(string text, int startIndex, char openChar, char closeChar)
    {
        if (startIndex < 0
            || startIndex >= text.Length
            || text[startIndex] != openChar)
        {
            return -1;
        }

        var depth = 0;
        for (int index = startIndex; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == openChar)
            {
                depth++;
            }
            else if (ch == closeChar)
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return -1;
    }

    private static int ReadJavaScriptTypeScriptIdentifierLength(string text, int startIndex)
    {
        if (startIndex < 0 || startIndex >= text.Length)
            return 0;

        var first = text[startIndex];
        if (!(char.IsLetter(first) || first is '_' or '$'))
            return 0;

        var index = startIndex + 1;
        while (index < text.Length)
        {
            var ch = text[index];
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '$'))
                break;

            index++;
        }

        return index - startIndex;
    }
}
