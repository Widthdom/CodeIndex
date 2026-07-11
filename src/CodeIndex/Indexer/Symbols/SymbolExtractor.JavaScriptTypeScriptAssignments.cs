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
}
