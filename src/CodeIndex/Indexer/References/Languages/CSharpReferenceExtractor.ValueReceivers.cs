using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void AddCSharpLambdaParametersBeforeArrow(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string bodyText,
        int arrowIndex,
        int startLineNumber,
        CSharpLineColumn scopeEnd)
    {
        var leftIndex = SkipWhitespaceBackward(bodyText, arrowIndex - 1);
        if (leftIndex < 0)
            return;

        var declarationLine = GetLineNumberFromOffset(bodyText, arrowIndex, startLineNumber);
        if (bodyText[leftIndex] == ')')
        {
            if (!TryFindMatchingOpenParen(bodyText, leftIndex, out var openParenIndex))
                return;

            var scopeStart = GetLineColumnFromOffset(bodyText, openParenIndex, startLineNumber);
            var parameters = bodyText[(openParenIndex + 1)..leftIndex];
            foreach (var segment in SplitTopLevelCSharpParameterSegments(parameters))
            {
                if (TryExtractTrailingCSharpParameterName(segment, out var parameterName))
                    AddCSharpFunctionValueReceiverName(names, parameterName, scopeStart.Line, scopeStart.Column, scopeEnd.Line, scopeEnd.Column);
            }

            return;
        }

        var identifierEnd = leftIndex + 1;
        var identifierStart = leftIndex;
        while (identifierStart >= 0 && IsCSharpIdentifierPart(bodyText[identifierStart]))
            identifierStart--;
        identifierStart++;
        if (identifierStart >= identifierEnd || !IsCSharpIdentifierStart(bodyText[identifierStart]))
            return;

        var parameter = NormalizeCSharpIdentifier(bodyText[identifierStart..identifierEnd]);
        var prefixIndex = SkipWhitespaceBackward(bodyText, identifierStart - 1);
        if (prefixIndex < 0)
            return;

        var prefixChar = bodyText[prefixIndex];
        if (prefixChar is '=' or '(' or ',' or ':'
            || (TryReadPreviousIdentifierToken(bodyText, prefixIndex, out var previousToken)
                && string.Equals(previousToken, "return", StringComparison.Ordinal)))
        {
            AddCSharpFunctionValueReceiverName(names, parameter, declarationLine, identifierStart - GetLineStartOffset(bodyText, arrowIndex), scopeEnd.Line, scopeEnd.Column);
        }
    }

    private static void AddCSharpFunctionValueReceiverName(List<CSharpFunctionValueReceiverNameRecord> names, string name, int scopeStartLine, int scopeStartColumn, int scopeEndLine, int scopeEndColumn)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (names.Any(record =>
            record.ScopeStartLine == scopeStartLine
            && record.ScopeStartColumn == scopeStartColumn
            && record.ScopeEndLine == scopeEndLine
            && record.ScopeEndColumn == scopeEndColumn
            && string.Equals(record.Name, name, StringComparison.Ordinal)))
            return;

        names.Add(new CSharpFunctionValueReceiverNameRecord(name, scopeStartLine, scopeStartColumn, scopeEndLine, scopeEndColumn));
    }

    private static int GetLineNumberFromOffset(string text, int offset, int startLineNumber)
    {
        var lineNumber = startLineNumber;
        var limit = Math.Min(offset, text.Length);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
                lineNumber++;
        }

        return lineNumber;
    }

    private static int FindInnermostCSharpBlockEndLine(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex,
        int declarationLineIndex,
        int declarationColumn)
    {
        var depth = 0;
        for (var lineIndex = bodyStartIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var limit = lineIndex == declarationLineIndex ? Math.Min(declarationColumn, line.Length) : line.Length;
            for (var column = 0; column < limit; column++)
            {
                if (line[column] == '{')
                    depth++;
                else if (line[column] == '}' && depth > 0)
                    depth--;
            }

            if (lineIndex != declarationLineIndex)
                continue;

            var declarationDepth = depth;
            for (var scanLine = declarationLineIndex; scanLine <= bodyEndIndex; scanLine++)
            {
                var scan = structuralLines[scanLine];
                var scanStart = scanLine == declarationLineIndex ? declarationColumn : 0;
                for (var column = scanStart; column < scan.Length; column++)
                {
                    if (scan[column] == '{')
                        depth++;
                    else if (scan[column] == '}' && depth > 0)
                    {
                        depth--;
                        if (depth < declarationDepth)
                            return scanLine + 1;
                    }
                }
            }

            break;
        }

        return bodyEndIndex + 1;
    }

    private static CSharpLineColumn FindFollowingCSharpEmbeddedStatementEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int headerLineIndex,
        int searchStartColumn)
    {
        var parenDepth = 0;
        var foundHeaderOpenParen = false;
        for (var lineIndex = headerLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var startColumn = lineIndex == headerLineIndex ? Math.Min(searchStartColumn, line.Length) : 0;
            for (var column = startColumn; column < line.Length; column++)
            {
                if (line[column] == '(')
                {
                    parenDepth++;
                    foundHeaderOpenParen = true;
                }
                else if (line[column] == ')' && foundHeaderOpenParen && parenDepth > 0)
                {
                    parenDepth--;
                    if (parenDepth == 0)
                        return FindCSharpStatementEndPosition(structuralLines, bodyEndIndex, lineIndex, column + 1);
                }
            }
        }

        return new CSharpLineColumn(bodyEndIndex + 1, 0);
    }

    private static bool TryFindCSharpDeclarationPatternScopeEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex,
        int lineIndex,
        int declarationColumn,
        out CSharpLineColumn scopeEnd)
    {
        scopeEnd = new CSharpLineColumn(0, 0);
        if (lineIndex < 0
            || lineIndex >= structuralLines.Count
            || bodyStartIndex < 0
            || bodyEndIndex < bodyStartIndex)
            return false;

        var bodyText = string.Join("\n", structuralLines.Skip(bodyStartIndex).Take(bodyEndIndex - bodyStartIndex + 1));
        if (string.IsNullOrEmpty(bodyText))
            return false;

        var targetOffset = GetBodyTextOffset(structuralLines, bodyStartIndex, bodyEndIndex, lineIndex, declarationColumn);
        var startLineNumber = bodyStartIndex + 1;
        if (TryFindCSharpConditionalExpressionScopeEndPosition(bodyText, startLineNumber, targetOffset, out scopeEnd))
            return true;

        if (TryFindEnclosingCSharpLambdaScopeEndPosition(
                bodyText,
                startLineNumber,
                bodyEndIndex + 1,
                targetOffset,
                out scopeEnd))
        {
            return true;
        }

        if (!TryFindCSharpConditionalHeaderStartPosition(structuralLines, bodyStartIndex, lineIndex, declarationColumn, out var headerLineIndex, out var headerStartColumn))
            return false;

        scopeEnd = FindFollowingCSharpEmbeddedStatementEndPosition(structuralLines, bodyEndIndex, headerLineIndex, headerStartColumn);
        return true;
    }

    private static bool TryFindCSharpSwitchCaseScopeEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int lineIndex,
        int declarationColumn,
        out CSharpLineColumn scopeEnd)
    {
        scopeEnd = new CSharpLineColumn(0, 0);
        if (lineIndex < 0 || lineIndex >= structuralLines.Count)
            return false;

        var labelLineIndex = -1;
        var labelColumn = -1;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var scanLine = lineIndex; scanLine <= bodyEndIndex; scanLine++)
        {
            var line = structuralLines[scanLine];
            var startColumn = scanLine == lineIndex ? Math.Min(Math.Max(declarationColumn, 0), line.Length) : 0;
            for (var column = startColumn; column < line.Length; column++)
            {
                var current = line[column];
                switch (current)
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
                    case ':':
                        if (parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0
                            && (column == 0 || line[column - 1] != ':')
                            && (column + 1 >= line.Length || line[column + 1] != ':'))
                        {
                            labelLineIndex = scanLine;
                            labelColumn = column;
                            break;
                        }

                        break;
                }

                if (labelLineIndex >= 0)
                    break;
            }

            if (labelLineIndex >= 0)
                break;
        }

        if (labelLineIndex < 0)
            return false;

        braceDepth = 0;
        for (var scanLine = labelLineIndex; scanLine <= bodyEndIndex; scanLine++)
        {
            var scan = structuralLines[scanLine];
            if (scanLine > labelLineIndex && braceDepth == 0 && IsCSharpSwitchLabelLine(scan))
            {
                scopeEnd = new CSharpLineColumn(scanLine + 1, 0);
                return true;
            }

            var startColumn = scanLine == labelLineIndex ? Math.Min(labelColumn + 1, scan.Length) : 0;
            for (var column = startColumn; column < scan.Length; column++)
            {
                var current = scan[column];
                if (current == '{')
                {
                    braceDepth++;
                }
                else if (current == '}')
                {
                    if (braceDepth == 0)
                    {
                        scopeEnd = new CSharpLineColumn(scanLine + 1, column);
                        return true;
                    }

                    braceDepth--;
                }
            }
        }

        scopeEnd = new CSharpLineColumn(bodyEndIndex + 1, structuralLines[Math.Min(bodyEndIndex, structuralLines.Count - 1)].Length);
        return true;
    }

    private static bool TryFindEnclosingCSharpLambdaScopeEndPosition(
        string bodyText,
        int startLineNumber,
        int fallbackScopeEndLine,
        int targetOffset,
        out CSharpLineColumn scopeEnd)
    {
        scopeEnd = new CSharpLineColumn(0, 0);
        if (string.IsNullOrEmpty(bodyText))
            return false;

        var foundEnclosingLambda = false;
        for (var searchIndex = 0; searchIndex < bodyText.Length;)
        {
            var arrowIndex = bodyText.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0 || arrowIndex >= targetOffset)
                break;

            searchIndex = arrowIndex + 2;
            if (!IsPotentialCSharpLambdaArrow(bodyText, arrowIndex))
                continue;

            var lambdaScopeEnd = FindCSharpArrowExpressionScopeEndPosition(bodyText, arrowIndex, startLineNumber, fallbackScopeEndLine);
            var lambdaScopeEndOffset = GetTextOffsetFromLineColumn(bodyText, startLineNumber, lambdaScopeEnd);
            if (targetOffset > lambdaScopeEndOffset)
                continue;

            scopeEnd = lambdaScopeEnd;
            foundEnclosingLambda = true;
        }

        return foundEnclosingLambda;
    }

    private static bool TryFindCSharpConditionalExpressionScopeEndPosition(
        string bodyText,
        int startLineNumber,
        int targetOffset,
        out CSharpLineColumn scopeEnd)
    {
        scopeEnd = new CSharpLineColumn(0, 0);
        if (string.IsNullOrEmpty(bodyText))
            return false;

        GetCSharpDelimiterDepthsAtOffset(bodyText, targetOffset, out var baseParenDepth, out var baseBracketDepth, out var baseBraceDepth);
        var parenDepth = baseParenDepth;
        var bracketDepth = baseBracketDepth;
        var braceDepth = baseBraceDepth;
        var questionIndex = -1;
        var nestedConditionalDepth = 0;
        for (var i = Math.Min(targetOffset, bodyText.Length); i < bodyText.Length; i++)
        {
            var current = bodyText[i];
            switch (current)
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

            var atBaseDepth = parenDepth == baseParenDepth
                && bracketDepth == baseBracketDepth
                && braceDepth == baseBraceDepth;
            if (!atBaseDepth)
                continue;

            if (questionIndex < 0)
            {
                if (IsCSharpConditionalOperatorQuestionMark(bodyText, i))
                {
                    questionIndex = i;
                    continue;
                }

                if (current is ';' or ',' or ')')
                    return false;

                continue;
            }

            if (IsCSharpConditionalOperatorQuestionMark(bodyText, i))
            {
                nestedConditionalDepth++;
                continue;
            }

            if (current != ':')
                continue;

            if (nestedConditionalDepth == 0)
            {
                scopeEnd = GetLineColumnFromOffset(bodyText, i, startLineNumber);
                return true;
            }

            nestedConditionalDepth--;
        }

        return false;
    }

    private static bool TryFindCSharpConditionalHeaderStartPosition(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int lineIndex,
        int declarationColumn,
        out int headerLineIndex,
        out int headerStartColumn)
    {
        headerLineIndex = -1;
        headerStartColumn = -1;
        if (lineIndex < bodyStartIndex || lineIndex >= structuralLines.Count)
            return false;

        for (var scanLine = lineIndex; scanLine >= bodyStartIndex; scanLine--)
        {
            var searchColumn = scanLine == lineIndex
                ? declarationColumn
                : structuralLines[scanLine].Length - 1;
            if (!TryFindCSharpConditionalHeaderStartColumn(structuralLines[scanLine], searchColumn, out var column))
                continue;

            headerLineIndex = scanLine;
            headerStartColumn = column;
            return true;
        }

        return false;
    }

    private static bool TryFindCSharpConditionalHeaderStartColumn(string line, int searchLimitColumn, out int headerStartColumn)
    {
        headerStartColumn = -1;
        if (string.IsNullOrEmpty(line))
            return false;

        var limit = Math.Min(searchLimitColumn, line.Length - 1);
        for (var column = limit; column >= 0; column--)
        {
            if (!TryConsumeCSharpKeyword(line, column, "if", out var afterKeyword)
                && !TryConsumeCSharpKeyword(line, column, "while", out afterKeyword))
            {
                continue;
            }

            var openParenColumn = line.IndexOf('(', afterKeyword);
            if (openParenColumn >= 0 && openParenColumn <= limit)
            {
                headerStartColumn = column;
                return true;
            }
        }

        return false;
    }

    private static bool IsCSharpSwitchLabelLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.TrimStart();
        return trimmed.StartsWith("case ", StringComparison.Ordinal)
            || string.Equals(trimmed, "default:", StringComparison.Ordinal)
            || trimmed.StartsWith("default:", StringComparison.Ordinal);
    }

    private static int SkipWhitespaceForward(string text, int index)
    {
        var current = Math.Max(index, 0);
        while (current < text.Length && char.IsWhiteSpace(text[current]))
            current++;

        return current;
    }

    private static int GetBodyTextOffset(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex,
        int lineIndex,
        int column)
    {
        if (bodyEndIndex < bodyStartIndex)
            return 0;

        var clampedLineIndex = Math.Max(bodyStartIndex, Math.Min(lineIndex, bodyEndIndex));
        var offset = 0;
        for (var scanLine = bodyStartIndex; scanLine < clampedLineIndex; scanLine++)
            offset += structuralLines[scanLine].Length + 1;

        var line = structuralLines[clampedLineIndex];
        return offset + Math.Max(0, Math.Min(column, line.Length));
    }

    private static CSharpLineColumn FindCSharpStatementEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn)
    {
        if (TrySkipCSharpWhitespace(structuralLines, bodyEndIndex, startLineIndex, startColumn, out var statementLineIndex, out var statementColumn)
            && TryConsumeCSharpKeyword(structuralLines[statementLineIndex], statementColumn, "if", out var afterIfColumn))
        {
            if (TrySkipCSharpWhitespace(structuralLines, bodyEndIndex, statementLineIndex, afterIfColumn, out var openParenLineIndex, out var openParenColumn)
                && openParenColumn < structuralLines[openParenLineIndex].Length
                && structuralLines[openParenLineIndex][openParenColumn] == '('
                && TryFindMatchingCSharpDelimiter(structuralLines, bodyEndIndex, openParenLineIndex, openParenColumn, '(', ')', out var closeParen))
            {
                var thenEnd = FindCSharpStatementEndPosition(structuralLines, bodyEndIndex, closeParen.Line, closeParen.Column + 1);
                if (TrySkipCSharpWhitespace(structuralLines, bodyEndIndex, thenEnd.Line - 1, thenEnd.Column + 1, out var elseLineIndex, out var elseColumn)
                    && TryConsumeCSharpKeyword(structuralLines[elseLineIndex], elseColumn, "else", out var afterElseColumn))
                {
                    return FindCSharpStatementEndPosition(structuralLines, bodyEndIndex, elseLineIndex, afterElseColumn);
                }

                return thenEnd;
            }
        }

        var foundContent = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (!foundContent)
                {
                    if (char.IsWhiteSpace(current))
                        continue;

                    foundContent = true;
                }

                switch (current)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (bracketDepth > 0)
                            bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                        {
                            braceDepth--;
                            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                                return new CSharpLineColumn(lineIndex + 1, column);
                        }
                        break;
                    case ';':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        break;
                    case ',':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        break;
                }
            }
        }

        return new CSharpLineColumn(bodyEndIndex + 1, 0);
    }

    private static bool TrySkipCSharpWhitespace(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn,
        out int nextLineIndex,
        out int nextColumn)
    {
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                if (!char.IsWhiteSpace(line[column]))
                {
                    nextLineIndex = lineIndex;
                    nextColumn = column;
                    return true;
                }
            }
        }

        nextLineIndex = bodyEndIndex;
        nextColumn = structuralLines[Math.Min(bodyEndIndex, structuralLines.Count - 1)].Length;
        return false;
    }

    private static bool TryConsumeCSharpKeyword(string line, int startColumn, string keyword, out int nextColumn)
    {
        nextColumn = startColumn;
        if (startColumn < 0 || startColumn + keyword.Length > line.Length)
            return false;
        if (!line.AsSpan(startColumn, keyword.Length).Equals(keyword, StringComparison.Ordinal))
            return false;
        if (startColumn > 0 && IsCSharpIdentifierPart(line[startColumn - 1]))
            return false;
        if (startColumn + keyword.Length < line.Length && IsCSharpIdentifierPart(line[startColumn + keyword.Length]))
            return false;

        nextColumn = startColumn + keyword.Length;
        return true;
    }

    private static bool TryConsumeCSharpQueryClauseKeyword(string line, int startColumn, out string keyword, out int nextColumn)
    {
        keyword = string.Empty;
        nextColumn = startColumn;
        if (startColumn < 0 || startColumn >= line.Length)
            return false;

        if (startColumn > 0)
        {
            if (!char.IsWhiteSpace(line[startColumn - 1]))
                return false;

            for (var probe = startColumn - 1; probe >= 0; probe--)
            {
                if (char.IsWhiteSpace(line[probe]))
                    continue;

                if (line[probe] == '.' || line[probe] == ':')
                    return false;

                break;
            }
        }

        var tokenStart = startColumn;
        if (line[tokenStart] == '@')
            return false;

        if (!IsCSharpIdentifierPart(line[tokenStart]))
        {
            return false;
        }

        var tokenEnd = tokenStart + 1;
        while (tokenEnd < line.Length && IsCSharpIdentifierPart(line[tokenEnd]))
            tokenEnd++;

        keyword = line.Substring(tokenStart, tokenEnd - tokenStart);
        nextColumn = tokenEnd;
        return true;
    }

    private static bool IsCSharpTerminalQueryClauseKeyword(string keyword)
    {
        return string.Equals(keyword, "select", StringComparison.Ordinal)
            || string.Equals(keyword, "group", StringComparison.Ordinal);
    }

    private static bool IsCSharpQueryClauseKeyword(string keyword)
    {
        return IsCSharpTerminalQueryClauseKeyword(keyword)
            || string.Equals(keyword, "from", StringComparison.Ordinal)
            || string.Equals(keyword, "let", StringComparison.Ordinal)
            || string.Equals(keyword, "where", StringComparison.Ordinal)
            || string.Equals(keyword, "orderby", StringComparison.Ordinal)
            || string.Equals(keyword, "join", StringComparison.Ordinal)
            || string.Equals(keyword, "on", StringComparison.Ordinal)
            || string.Equals(keyword, "equals", StringComparison.Ordinal)
            || string.Equals(keyword, "by", StringComparison.Ordinal)
            || string.Equals(keyword, "into", StringComparison.Ordinal)
            || string.Equals(keyword, "ascending", StringComparison.Ordinal)
            || string.Equals(keyword, "descending", StringComparison.Ordinal);
    }

    private static bool IsCSharpQueryClauseKeywordSuffix(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int lineIndex,
        string line,
        int nextColumn,
        string keyword,
        int previousTopLevelSignificantLineIndex,
        int previousTopLevelSignificantColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (IsCSharpParenthesizedQueryClauseKeyword(keyword)
            && TryGetNextTopLevelSignificantChar(
                structuralLines,
                lineIndex,
                nextColumn,
                out _,
                out _,
                out var nextTopLevelSignificantChar)
            && nextTopLevelSignificantChar == '(')
        {
            return CanStartCSharpParenthesizedQueryClause(
                structuralLines,
                bodyEndIndex,
                previousTopLevelSignificantLineIndex,
                previousTopLevelSignificantColumn,
                csharpKnownTypeNames,
                csharpUsingAliases,
                csharpFunctionValueReceiverNames);
        }

        if (nextColumn >= line.Length)
            return true;

        var next = line[nextColumn];
        if (char.IsWhiteSpace(next))
            return true;

        return (string.Equals(keyword, "ascending", StringComparison.Ordinal)
                || string.Equals(keyword, "descending", StringComparison.Ordinal))
            && (next == ',' || next == ')' || next == ']' || next == '}' || next == ';');
    }

    private static bool IsCSharpParenthesizedQueryClauseKeyword(string keyword)
    {
        return string.Equals(keyword, "select", StringComparison.Ordinal)
            || string.Equals(keyword, "group", StringComparison.Ordinal)
            || string.Equals(keyword, "orderby", StringComparison.Ordinal);
    }

    private static bool CanStartCSharpParenthesizedQueryClause(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int previousTopLevelSignificantLineIndex,
        int previousTopLevelSignificantColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (previousTopLevelSignificantLineIndex < 0 || previousTopLevelSignificantColumn < 0)
            return true;

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                previousTopLevelSignificantLineIndex,
                previousTopLevelSignificantColumn,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out var previousTokenEndColumn,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken))
            return !IsCSharpParenthesizedQueryClausePrefixIdentifier(
                structuralLines[previousTokenLineIndex],
                previousTokenStartColumn,
                previousIdentifierToken);

        return previousPunctuationToken switch
        {
            '(' or '[' or '{' or ',' or ';' or ':' or '*' or '/' or '%' or '&' or '|' or '^' or '=' or '~' or '<' => false,
            ')' => !LooksLikeCSharpCastCloseParen(
                structuralLines,
                previousTokenLineIndex,
                previousTokenStartColumn,
                csharpKnownTypeNames,
                csharpUsingAliases,
                csharpFunctionValueReceiverNames),
            '?' => LooksLikeCSharpNullableTypeSuffixInCastOrTypeTest(
                structuralLines,
                previousTokenLineIndex,
                previousTokenStartColumn),
            '+' or '-' => CanStartCSharpParenthesizedQueryClauseAfterPlusOrMinus(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn,
                previousTokenEndColumn,
                previousPunctuationToken),
            '!' => CanStartCSharpParenthesizedQueryClauseAfterBang(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn),
            '>' => LooksLikeCSharpQueryGenericTypeArgumentClose(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn),
            _ => true
        };
    }

    private static bool LooksLikeCSharpCastCloseParen(
        IReadOnlyList<string> structuralLines,
        int closeParenLineIndex,
        int closeParenColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (!TryFindMatchingCSharpOpenParenBackwards(
                structuralLines,
                closeParenLineIndex,
                closeParenColumn,
                out var openParenLineIndex,
                out var openParenColumn))
        {
            return false;
        }

        var castTargetText = GetCSharpTextBetween(
            structuralLines,
            openParenLineIndex,
            openParenColumn + 1,
            closeParenLineIndex,
            closeParenColumn);
        if (!LooksLikeCSharpCastTypeText(
                castTargetText,
                closeParenLineIndex + 1,
                closeParenColumn,
                csharpKnownTypeNames,
                csharpUsingAliases,
                csharpFunctionValueReceiverNames))
            return false;

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                openParenLineIndex,
                openParenColumn - 1,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out _,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken))
            return IsCSharpCastPrefixIdentifier(structuralLines[previousTokenLineIndex], previousTokenStartColumn, previousIdentifierToken);

        return previousPunctuationToken is not (')' or ']' or '}' or '"' or '\'' or '>');
    }

    private static bool IsCSharpCastPrefixIdentifier(string line, int tokenStartColumn, string token)
    {
        if (tokenStartColumn > 0 && line[tokenStartColumn - 1] == '@')
            return false;

        return string.Equals(token, "return", StringComparison.Ordinal)
            || string.Equals(token, "await", StringComparison.Ordinal)
            || string.Equals(token, "throw", StringComparison.Ordinal)
            || IsCSharpQueryClauseKeyword(token);
    }

    private static bool LooksLikeCSharpCastTypeText(
        string text,
        int lineNumber,
        int column,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return false;

        var index = 0;
        if (!TryConsumeCSharpCastType(trimmed, ref index))
            return false;

        SkipCSharpCastTypeWhitespace(trimmed, ref index);
        if (index != trimmed.Length)
            return false;

        var shape = AnalyzeCSharpCastTypeShape(trimmed);
        if (shape.IdentifierSegments.Count == 0)
            return shape.HasTypeOnlySyntax;

        var resolvedQualifiedName = shape.SimpleQualifiedName == null
            ? null
            : ResolveCSharpQualifiedAliasTarget(shape.SimpleQualifiedName, lineNumber, csharpUsingAliases);
        var resolvedBareName = resolvedQualifiedName == null
            ? null
            : ExtractBareTypeName(resolvedQualifiedName);

        var lastSegment = shape.IdentifierSegments[^1];
        if (HasKnownNonTerminalTypeSegment(shape.IdentifierSegments, csharpKnownTypeNames)
            && !IsKnownCSharpCastTypeName(lastSegment, resolvedBareName, csharpKnownTypeNames))
        {
            return false;
        }

        if (IsKnownCSharpCastTypeName(lastSegment, resolvedBareName, csharpKnownTypeNames)
            || (!string.IsNullOrWhiteSpace(resolvedQualifiedName) && csharpKnownTypeNames.Contains(resolvedQualifiedName)))
        {
            return true;
        }

        if (shape.SimpleQualifiedName != null
            && string.Equals(shape.SimpleQualifiedName, resolvedQualifiedName, StringComparison.Ordinal)
            && HasCSharpFunctionValueReceiverConflict(
                GetFirstQualifiedSegment(shape.SimpleQualifiedName),
                lineNumber,
                column,
                csharpFunctionValueReceiverNames))
        {
            return false;
        }

        if (shape.HasTypeOnlySyntax)
            return true;

        return shape.AllIdentifiersTypeLike && shape.IdentifierSegments.Count <= 2;
    }

    private static bool TryConsumeCSharpCastType(string text, ref int index)
    {
        if (!TryConsumeCSharpCastTypeCore(text, ref index))
            return false;

        while (true)
        {
            var checkpoint = index;
            SkipCSharpCastTypeWhitespace(text, ref index);
            if (TryConsumeCSharpCastArraySuffix(text, ref index)
                || TryConsumeCSharpCastNullableSuffix(text, ref index))
            {
                continue;
            }

            index = checkpoint;
            return true;
        }
    }

    private static bool TryConsumeCSharpCastTypeCore(string text, ref int index)
    {
        SkipCSharpCastTypeWhitespace(text, ref index);
        if (index < text.Length && text[index] == '(')
            return TryConsumeCSharpCastTupleType(text, ref index);

        return TryConsumeCSharpCastQualifiedType(text, ref index);
    }

    private static bool TryConsumeCSharpCastQualifiedType(string text, ref int index)
    {
        if (!TryConsumeCSharpCastIdentifier(text, ref index, out var token))
            return false;

        if (!TryConsumeCSharpCastGenericArgumentList(text, ref index))
            return false;

        while (true)
        {
            var checkpoint = index;
            SkipCSharpCastTypeWhitespace(text, ref index);
            if (!TryConsumeCSharpCastQualifiedTypeSeparator(text, ref index))
            {
                index = checkpoint;
                return true;
            }

            if (!TryConsumeCSharpCastIdentifier(text, ref index, out token))
                return false;

            if (!TryConsumeCSharpCastGenericArgumentList(text, ref index))
                return false;
        }
    }

    private static bool TryConsumeCSharpCastTupleType(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '(')
            return false;

        index++;
        while (true)
        {
            if (!TryConsumeCSharpCastType(text, ref index))
                return false;

            var checkpoint = index;
            if (TryConsumeCSharpCastIdentifier(text, ref index, out _))
            {
                // Tuple element names are optional and do not affect type-likeness.
            }
            else
            {
                index = checkpoint;
            }

            SkipCSharpCastTypeWhitespace(text, ref index);
            if (index >= text.Length)
                return false;

            if (text[index] == ')')
            {
                index++;
                return true;
            }

            if (text[index] != ',')
                return false;

            index++;
        }
    }

    private static bool TryConsumeCSharpCastGenericArgumentList(string text, ref int index)
    {
        var checkpoint = index;
        SkipCSharpCastTypeWhitespace(text, ref index);
        if (index >= text.Length || text[index] != '<')
        {
            index = checkpoint;
            return true;
        }

        index++;
        while (true)
        {
            if (!TryConsumeCSharpCastType(text, ref index))
                return false;

            SkipCSharpCastTypeWhitespace(text, ref index);
            if (index >= text.Length)
                return false;

            if (text[index] == '>')
            {
                index++;
                return true;
            }

            if (text[index] != ',')
                return false;

            index++;
        }
    }

    private static bool TryConsumeCSharpCastArraySuffix(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '[')
            return false;

        index++;
        SkipCSharpCastTypeWhitespace(text, ref index);
        while (index < text.Length && text[index] == ',')
        {
            index++;
            SkipCSharpCastTypeWhitespace(text, ref index);
        }

        if (index >= text.Length || text[index] != ']')
            return false;

        index++;
        return true;
    }

    private static bool TryConsumeCSharpCastNullableSuffix(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '?')
            return false;

        index++;
        return true;
    }

    private static bool TryConsumeCSharpCastQualifiedTypeSeparator(string text, ref int index)
    {
        if (index >= text.Length)
            return false;

        if (text[index] == '.')
        {
            index++;
            return true;
        }

        if (index + 1 < text.Length && text[index] == ':' && text[index + 1] == ':')
        {
            index += 2;
            return true;
        }

        return false;
    }

    private static bool TryConsumeCSharpCastIdentifier(string text, ref int index, out string token)
    {
        SkipCSharpCastTypeWhitespace(text, ref index);
        token = string.Empty;
        if (index >= text.Length)
            return false;

        var start = index;
        if (text[index] == '@')
        {
            index++;
            if (index >= text.Length || !IsCSharpIdentifierStart(text[index]))
            {
                index = start;
                return false;
            }
        }
        else if (!IsCSharpIdentifierStart(text[index]))
        {
            return false;
        }

        index++;
        while (index < text.Length && IsCSharpIdentifierPart(text[index]))
            index++;

        token = text.Substring(start, index - start);
        return true;
    }

    private static CSharpCastTypeShape AnalyzeCSharpCastTypeShape(string text)
    {
        var segments = new List<string>();
        var simpleQualifiedName = new System.Text.StringBuilder();
        var hasTypeOnlySyntax = false;
        var allIdentifiersTypeLike = true;
        var simpleQualifiedCandidate = true;

        for (var index = 0; index < text.Length;)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '@' || IsCSharpIdentifierStart(current))
            {
                var start = index;
                if (current == '@')
                    index++;
                if (index < text.Length)
                    index++;
                while (index < text.Length && IsCSharpIdentifierPart(text[index]))
                    index++;

                var token = text.Substring(start, index - start);
                segments.Add(token);
                allIdentifiersTypeLike &= IsLikelyCSharpTypeIdentifier(token);
                if (simpleQualifiedCandidate)
                    simpleQualifiedName.Append(token);
                continue;
            }

            switch (current)
            {
                case '.':
                    if (simpleQualifiedCandidate)
                        simpleQualifiedName.Append(current);
                    index++;
                    continue;
                case ':':
                    if (index + 1 < text.Length && text[index + 1] == ':')
                    {
                        hasTypeOnlySyntax = true;
                        if (simpleQualifiedCandidate)
                            simpleQualifiedName.Append("::");
                        index += 2;
                        continue;
                    }

                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                case '<':
                case '[':
                case '?':
                case '(':
                    hasTypeOnlySyntax = true;
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                case '>':
                case ']':
                case ')':
                case ',':
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
                default:
                    simpleQualifiedCandidate = false;
                    index++;
                    continue;
            }
        }

        return new CSharpCastTypeShape(
            segments,
            simpleQualifiedCandidate && simpleQualifiedName.Length > 0 ? simpleQualifiedName.ToString() : null,
            hasTypeOnlySyntax,
            allIdentifiersTypeLike);
    }

    private static bool HasKnownNonTerminalTypeSegment(IReadOnlyList<string> segments, IReadOnlySet<string> csharpKnownTypeNames)
    {
        for (var index = 0; index < segments.Count - 1; index++)
        {
            if (csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(segments[index])))
                return true;
        }

        return false;
    }

    private static bool IsKnownCSharpCastTypeName(string candidate, string? resolvedCandidate, IReadOnlySet<string> csharpKnownTypeNames)
    {
        return csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(candidate))
            || (!string.IsNullOrWhiteSpace(resolvedCandidate) && csharpKnownTypeNames.Contains(NormalizeCSharpIdentifier(resolvedCandidate)));
    }

    private static bool HasCSharpFunctionValueReceiverConflict(
        string candidate,
        int lineNumber,
        int column,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        if (string.IsNullOrWhiteSpace(candidate) || csharpFunctionValueReceiverNames.Count == 0)
            return false;

        var normalizedCandidate = NormalizeCSharpIdentifier(candidate);
        return csharpFunctionValueReceiverNames.Any(record =>
            IsWithinCSharpScope(record, lineNumber, column)
            && string.Equals(record.Name, normalizedCandidate, StringComparison.Ordinal));
    }

    private static bool IsLikelyCSharpTypeIdentifier(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var normalized = token[0] == '@' ? token.Substring(1) : token;
        if (normalized.Length == 0)
            return false;

        return IsCSharpBuiltInTypeKeyword(normalized)
            || char.IsUpper(normalized[0]);
    }

    private static void SkipCSharpCastTypeWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }

    private static bool IsCSharpBuiltInTypeKeyword(string text)
    {
        return string.Equals(text, "bool", StringComparison.Ordinal)
            || string.Equals(text, "byte", StringComparison.Ordinal)
            || string.Equals(text, "sbyte", StringComparison.Ordinal)
            || string.Equals(text, "short", StringComparison.Ordinal)
            || string.Equals(text, "ushort", StringComparison.Ordinal)
            || string.Equals(text, "int", StringComparison.Ordinal)
            || string.Equals(text, "uint", StringComparison.Ordinal)
            || string.Equals(text, "long", StringComparison.Ordinal)
            || string.Equals(text, "ulong", StringComparison.Ordinal)
            || string.Equals(text, "nint", StringComparison.Ordinal)
            || string.Equals(text, "nuint", StringComparison.Ordinal)
            || string.Equals(text, "char", StringComparison.Ordinal)
            || string.Equals(text, "float", StringComparison.Ordinal)
            || string.Equals(text, "double", StringComparison.Ordinal)
            || string.Equals(text, "decimal", StringComparison.Ordinal)
            || string.Equals(text, "string", StringComparison.Ordinal)
            || string.Equals(text, "object", StringComparison.Ordinal)
            || string.Equals(text, "dynamic", StringComparison.Ordinal);
    }

    private static bool CanStartCSharpParenthesizedQueryClauseAfterPlusOrMinus(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int operatorLineIndex,
        int operatorColumn,
        int operatorEndColumn,
        char operatorToken)
    {
        if (operatorLineIndex < 0 || operatorColumn < 0)
            return false;

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                operatorLineIndex,
                operatorColumn - 1,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out var previousTokenEndColumn,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken)
            || previousPunctuationToken != operatorToken
            || previousTokenLineIndex != operatorLineIndex
            || previousTokenEndColumn != operatorEndColumn - 1)
        {
            return false;
        }

        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                previousTokenLineIndex,
                previousTokenStartColumn - 1,
                out var operandTokenLineIndex,
                out var operandTokenStartColumn,
                out _,
                out var operandIdentifierToken,
                out var operandPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(operandIdentifierToken))
            return true;

        return operandPunctuationToken switch
        {
            ')' or ']' or '}' or '"' or '\'' => true,
            '>' => LooksLikeCSharpQueryGenericTypeArgumentClose(
                structuralLines,
                bodyEndIndex,
                operandTokenLineIndex,
                operandTokenStartColumn),
            _ => false
        };
    }

    private static bool CanStartCSharpParenthesizedQueryClauseAfterBang(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int bangLineIndex,
        int bangColumn)
    {
        if (!TryGetPreviousTopLevelToken(
                structuralLines,
                bangLineIndex,
                bangColumn - 1,
                out var previousTokenLineIndex,
                out var previousTokenStartColumn,
                out _,
                out var previousIdentifierToken,
                out var previousPunctuationToken))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(previousIdentifierToken))
            return !IsCSharpParenthesizedQueryClausePrefixIdentifier(
                structuralLines[previousTokenLineIndex],
                previousTokenStartColumn,
                previousIdentifierToken);

        return previousPunctuationToken switch
        {
            ')' or ']' or '}' or '"' or '\'' => true,
            '>' => LooksLikeCSharpQueryGenericTypeArgumentClose(
                structuralLines,
                bodyEndIndex,
                previousTokenLineIndex,
                previousTokenStartColumn),
            _ => false
        };
    }

    private static bool IsCSharpParenthesizedQueryClausePrefixIdentifier(string line, int tokenStartColumn, string token)
    {
        if (tokenStartColumn > 0 && line[tokenStartColumn - 1] == '@')
            return false;

        return string.Equals(token, "await", StringComparison.Ordinal)
            || string.Equals(token, "throw", StringComparison.Ordinal)
            || IsCSharpQueryClauseKeyword(token);
    }

    private static bool LooksLikeCSharpNullableTypeSuffixInCastOrTypeTest(
        IReadOnlyList<string> structuralLines,
        int questionLineIndex,
        int questionColumn)
    {
        var angleDepth = 0;
        var bracketDepth = 0;
        var parenDepth = 0;
        var currentLineIndex = questionLineIndex;
        var currentColumn = questionColumn - 1;
        while (TryGetPreviousTopLevelToken(
                   structuralLines,
                   currentLineIndex,
                   currentColumn,
                   out var tokenLineIndex,
                   out var tokenStartColumn,
                   out _,
                   out var identifierToken,
                   out var punctuationToken))
        {
            if (!string.IsNullOrEmpty(identifierToken))
            {
                if (angleDepth == 0
                    && bracketDepth == 0
                    && parenDepth == 0
                    && (string.Equals(identifierToken, "as", StringComparison.Ordinal)
                        || string.Equals(identifierToken, "is", StringComparison.Ordinal)))
                {
                    return true;
                }

                currentLineIndex = tokenLineIndex;
                currentColumn = tokenStartColumn - 1;
                continue;
            }

            switch (punctuationToken)
            {
                case '.':
                case '?':
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ',':
                    if (angleDepth > 0 || bracketDepth > 0 || parenDepth > 0)
                    {
                        currentLineIndex = tokenLineIndex;
                        currentColumn = tokenStartColumn - 1;
                        continue;
                    }

                    return false;
                case '>':
                    angleDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '<':
                    if (angleDepth == 0)
                        return false;

                    angleDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ']':
                    bracketDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '[':
                    if (bracketDepth == 0)
                        return false;

                    bracketDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case ')':
                    parenDepth++;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                case '(':
                    if (parenDepth == 0)
                        return false;

                    parenDepth--;
                    currentLineIndex = tokenLineIndex;
                    currentColumn = tokenStartColumn - 1;
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool TryGetPreviousTopLevelToken(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int startColumn,
        out int tokenLineIndex,
        out int tokenStartColumn,
        out int tokenEndColumn,
        out string identifierToken,
        out char punctuationToken)
    {
        tokenLineIndex = -1;
        tokenStartColumn = -1;
        tokenEndColumn = -1;
        identifierToken = string.Empty;
        punctuationToken = '\0';

        if (!TryGetPreviousTopLevelSignificantChar(
                structuralLines,
                startLineIndex,
                startColumn,
                out tokenLineIndex,
                out tokenEndColumn,
                out var tokenChar))
        {
            return false;
        }

        tokenStartColumn = tokenEndColumn;
        if (IsCSharpIdentifierPart(tokenChar))
        {
            var line = structuralLines[tokenLineIndex];
            while (tokenStartColumn > 0 && IsCSharpIdentifierPart(line[tokenStartColumn - 1]))
                tokenStartColumn--;

            identifierToken = line.Substring(tokenStartColumn, tokenEndColumn - tokenStartColumn + 1);
        }
        else
        {
            punctuationToken = tokenChar;
        }

        return true;
    }

    private static bool TryGetPreviousTopLevelSignificantChar(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int startColumn,
        out int lineIndex,
        out int column,
        out char value)
    {
        lineIndex = -1;
        column = -1;
        value = '\0';

        if (structuralLines.Count == 0)
            return false;

        var clampedLineIndex = Math.Min(startLineIndex, structuralLines.Count - 1);
        for (var currentLineIndex = clampedLineIndex; currentLineIndex >= 0; currentLineIndex--)
        {
            var line = structuralLines[currentLineIndex];
            var currentColumn = currentLineIndex == clampedLineIndex
                ? Math.Min(startColumn, line.Length - 1)
                : line.Length - 1;
            for (var probe = currentColumn; probe >= 0; probe--)
            {
                if (char.IsWhiteSpace(line[probe]))
                    continue;

                lineIndex = currentLineIndex;
                column = probe;
                value = line[probe];
                return true;
            }
        }

        return false;
    }

    private static bool TryGetNextTopLevelSignificantChar(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int startColumn,
        out int lineIndex,
        out int column,
        out char value)
    {
        lineIndex = -1;
        column = -1;
        value = '\0';

        if (structuralLines.Count == 0)
            return false;

        var clampedLineIndex = Math.Max(0, Math.Min(startLineIndex, structuralLines.Count - 1));
        for (var currentLineIndex = clampedLineIndex; currentLineIndex < structuralLines.Count; currentLineIndex++)
        {
            var line = structuralLines[currentLineIndex];
            var currentColumn = currentLineIndex == clampedLineIndex
                ? Math.Max(0, startColumn)
                : 0;
            for (var probe = currentColumn; probe < line.Length; probe++)
            {
                if (char.IsWhiteSpace(line[probe]))
                    continue;

                lineIndex = currentLineIndex;
                column = probe;
                value = line[probe];
                return true;
            }
        }

        return false;
    }

    private static bool TryFindMatchingCSharpOpenParenBackwards(
        IReadOnlyList<string> structuralLines,
        int closeParenLineIndex,
        int closeParenColumn,
        out int openParenLineIndex,
        out int openParenColumn)
    {
        openParenLineIndex = -1;
        openParenColumn = -1;

        var depth = 1;
        for (var lineIndex = closeParenLineIndex; lineIndex >= 0; lineIndex--)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == closeParenLineIndex ? Math.Min(closeParenColumn - 1, line.Length - 1) : line.Length - 1;
            for (var column = columnStart; column >= 0; column--)
            {
                switch (line[column])
                {
                    case ')':
                        depth++;
                        break;
                    case '(':
                        depth--;
                        if (depth == 0)
                        {
                            openParenLineIndex = lineIndex;
                            openParenColumn = column;
                            return true;
                        }

                        break;
                }
            }
        }

        return false;
    }

    private static string GetCSharpTextBetween(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int startColumn,
        int endLineIndex,
        int endColumn)
    {
        var builder = new System.Text.StringBuilder();
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var segmentStart = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            var segmentEnd = lineIndex == endLineIndex ? Math.Min(endColumn, line.Length) : line.Length;
            if (segmentStart < segmentEnd)
                builder.Append(line, segmentStart, segmentEnd - segmentStart);
            if (lineIndex < endLineIndex)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    private static bool LooksLikeCSharpQueryGenericTypeArgumentClose(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int closeLineIndex,
        int closeColumn)
    {
        if (closeLineIndex < 0 || closeLineIndex >= structuralLines.Count)
            return false;

        var angleDepth = 1;
        for (var lineIndex = closeLineIndex; lineIndex >= 0; lineIndex--)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == closeLineIndex ? Math.Min(closeColumn - 1, line.Length - 1) : line.Length - 1;
            for (var column = columnStart; column >= 0; column--)
            {
                var current = line[column];
                switch (current)
                {
                    case '>':
                        angleDepth++;
                        break;
                    case '<':
                        angleDepth--;
                        if (angleDepth == 0)
                            return LooksLikeCSharpQueryGenericTypeArgumentStart(structuralLines, bodyEndIndex, lineIndex, column);
                        break;
                }
            }
        }

        return false;
    }

    private static bool TryFindMatchingCSharpDelimiter(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn,
        char open,
        char close,
        out CSharpLineColumn match)
    {
        var depth = 0;
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (current == open)
                {
                    depth++;
                }
                else if (current == close && depth > 0)
                {
                    depth--;
                    if (depth == 0)
                    {
                        match = new CSharpLineColumn(lineIndex + 1, column);
                        return true;
                    }
                }
            }
        }

        match = new CSharpLineColumn(bodyEndIndex + 1, 0);
        return false;
    }

    private static CSharpLineColumn FindCSharpQueryExpressionEndPosition(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn,
        IReadOnlySet<string> csharpKnownTypeNames,
        IReadOnlyList<CSharpUsingAliasRecord> csharpUsingAliases,
        IReadOnlyList<CSharpFunctionValueReceiverNameRecord> csharpFunctionValueReceiverNames)
    {
        var foundContent = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;
        var terminalClauseSeen = false;
        var queryClauseSeen = false;
        var clauseHasTopLevelExpressionContent = false;
        var lastTopLevelSignificantLineIndex = -1;
        var lastTopLevelSignificantColumn = -1;

        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? Math.Min(startColumn, line.Length) : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (!foundContent)
                {
                    if (char.IsWhiteSpace(current))
                        continue;

                    foundContent = true;
                }

                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0
                    && TryConsumeCSharpQueryClauseKeyword(line, column, out var keyword, out var nextColumn))
                {
                    if ((!queryClauseSeen || clauseHasTopLevelExpressionContent)
                        && IsCSharpQueryClauseKeyword(keyword)
                        && IsCSharpQueryClauseKeywordSuffix(
                            structuralLines,
                            bodyEndIndex,
                            lineIndex,
                            line,
                            nextColumn,
                            keyword,
                            lastTopLevelSignificantLineIndex,
                            lastTopLevelSignificantColumn,
                            csharpKnownTypeNames,
                            csharpUsingAliases,
                            csharpFunctionValueReceiverNames))
                    {
                        if ((string.Equals(keyword, "by", StringComparison.Ordinal)
                                || string.Equals(keyword, "ascending", StringComparison.Ordinal)
                                || string.Equals(keyword, "descending", StringComparison.Ordinal))
                            && terminalClauseSeen)
                        {
                            terminalClauseSeen = true;
                        }
                        else
                        {
                            terminalClauseSeen = IsCSharpTerminalQueryClauseKeyword(keyword);
                        }

                        queryClauseSeen = true;
                        clauseHasTopLevelExpressionContent = false;
                        lastTopLevelSignificantLineIndex = lineIndex;
                        lastTopLevelSignificantColumn = nextColumn - 1;
                        column = nextColumn - 1;
                        continue;
                    }
                }

                switch (current)
                {
                    case '<':
                        if (parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0
                            && LooksLikeCSharpQueryGenericTypeArgumentStart(structuralLines, bodyEndIndex, lineIndex, column))
                        {
                            angleDepth++;
                        }
                        break;
                    case '>':
                        if (angleDepth > 0)
                        {
                            angleDepth--;
                        }
                        break;
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
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
                    case ';':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        break;
                    case ',':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0 && terminalClauseSeen)
                            return new CSharpLineColumn(lineIndex + 1, column);
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                        {
                            clauseHasTopLevelExpressionContent = false;
                            lastTopLevelSignificantLineIndex = lineIndex;
                            lastTopLevelSignificantColumn = column;
                        }
                        break;
                }

                if (!char.IsWhiteSpace(current)
                    && parenDepth == 0
                    && bracketDepth == 0
                    && braceDepth == 0
                    && angleDepth == 0
                    && current != ','
                    && current != ';')
                {
                    clauseHasTopLevelExpressionContent = true;
                    lastTopLevelSignificantLineIndex = lineIndex;
                    lastTopLevelSignificantColumn = column;
                }
            }
        }

        return new CSharpLineColumn(bodyEndIndex + 1, 0);
    }

    private static bool LooksLikeCSharpQueryGenericTypeArgumentStart(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn)
    {
        var line = structuralLines[startLineIndex];
        if (startColumn < 0 || startColumn >= line.Length || line[startColumn] != '<')
            return false;
        if (HasCSharpQueryGenericOperatorOnRight(line, startColumn + 1))
            return false;
        if (!HasCSharpQueryGenericReceiverOnLeft(line, startColumn - 1))
            return false;

        var angleDepth = 1;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var currentLine = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? startColumn + 1 : 0;
            for (var column = columnStart; column < currentLine.Length; column++)
            {
                var current = currentLine[column];
                switch (current)
                {
                    case '<':
                        angleDepth++;
                        break;
                    case '>':
                        angleDepth--;
                        if (angleDepth == 0)
                            return HasCSharpQueryGenericSuffix(structuralLines, bodyEndIndex, lineIndex, column + 1);
                        break;
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth == 0)
                            return false;
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (bracketDepth == 0)
                            return false;
                        bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth == 0)
                            return false;
                        braceDepth--;
                        break;
                    case ';':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return false;
                        break;
                }
            }
        }

        return false;
    }

    private static bool HasCSharpQueryGenericOperatorOnRight(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length)
            return false;

        return line[index] is '<' or '=';
    }

    private static bool HasCSharpQueryGenericReceiverOnLeft(string line, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(line[index]))
            index--;
        if (index < 0)
            return false;

        var current = line[index];
        return IsCSharpIdentifierPart(current) || current is '>' or ']' or ')';
    }

    private static bool HasCSharpQueryGenericSuffix(
        IReadOnlyList<string> structuralLines,
        int bodyEndIndex,
        int startLineIndex,
        int startColumn)
    {
        for (var lineIndex = startLineIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var columnStart = lineIndex == startLineIndex ? startColumn : 0;
            for (var column = columnStart; column < line.Length; column++)
            {
                var current = line[column];
                if (char.IsWhiteSpace(current))
                    continue;

                if (current is '(' or ')' or ']' or '[' or '.' or ',' or ';' or '{' or ':' or '?'
                    || IsCSharpIdentifierStart(current))
                {
                    return true;
                }

                return IsCSharpQueryGenericComparisonOperator(line, column);
            }
        }

        return true;
    }

    private static bool IsCSharpQueryGenericComparisonOperator(string line, int column)
    {
        if (column < 0 || column + 1 >= line.Length)
            return false;

        var current = line[column];
        return (current is '!' or '=') && line[column + 1] == '=';
    }

    private static CSharpLineColumn FindCSharpArrowExpressionScopeEndPosition(string bodyText, int arrowIndex, int startLineNumber, int fallbackScopeEndLine)
    {
        var foundContent = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var i = Math.Min(arrowIndex + 2, bodyText.Length); i < bodyText.Length; i++)
        {
            var current = bodyText[i];
            if (!foundContent)
            {
                if (char.IsWhiteSpace(current))
                    continue;

                foundContent = true;
            }

            switch (current)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return GetLineColumnFromOffset(bodyText, i, startLineNumber);
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return GetLineColumnFromOffset(bodyText, i, startLineNumber);
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                        return GetLineColumnFromOffset(bodyText, i + 1, startLineNumber);
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                        if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                            return GetLineColumnFromOffset(bodyText, i + 1, startLineNumber);
                    }
                    break;
                case ',':
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return GetLineColumnFromOffset(bodyText, i, startLineNumber);
                    break;
            }
        }

        return new CSharpLineColumn(fallbackScopeEndLine, int.MaxValue);
    }

    private static bool IsCSharpConditionalOperatorQuestionMark(string bodyText, int index)
    {
        if (index < 0 || index >= bodyText.Length || bodyText[index] != '?')
            return false;

        var previous = index > 0 ? bodyText[index - 1] : '\0';
        var next = index + 1 < bodyText.Length ? bodyText[index + 1] : '\0';
        return previous != '?'
            && next is not '?' and not '.' and not '[';
    }

    private static void GetCSharpDelimiterDepthsAtOffset(
        string bodyText,
        int offset,
        out int parenDepth,
        out int bracketDepth,
        out int braceDepth)
    {
        parenDepth = 0;
        bracketDepth = 0;
        braceDepth = 0;
        var limit = Math.Min(offset, bodyText.Length);
        for (var i = 0; i < limit; i++)
        {
            switch (bodyText[i])
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
        }
    }

    private static int GetTextOffsetFromLineColumn(string bodyText, int startLineNumber, CSharpLineColumn position)
    {
        if (string.IsNullOrEmpty(bodyText))
            return 0;

        if (position.Line <= startLineNumber)
            return Math.Max(0, Math.Min(position.Column, bodyText.Length));

        var currentLineNumber = startLineNumber;
        var lineStartOffset = 0;
        while (lineStartOffset < bodyText.Length && currentLineNumber < position.Line)
        {
            var newlineIndex = bodyText.IndexOf('\n', lineStartOffset);
            if (newlineIndex < 0)
                return bodyText.Length;

            currentLineNumber++;
            lineStartOffset = newlineIndex + 1;
        }

        var lineEndOffset = bodyText.IndexOf('\n', lineStartOffset);
        if (lineEndOffset < 0)
            lineEndOffset = bodyText.Length;

        return Math.Min(lineStartOffset + Math.Max(position.Column, 0), lineEndOffset);
    }

    private static bool IsPotentialCSharpLambdaArrow(string bodyText, int arrowIndex)
    {
        var leftIndex = SkipWhitespaceBackward(bodyText, arrowIndex - 1);
        if (leftIndex < 0)
            return false;

        if (bodyText[leftIndex] == ')')
        {
            if (!TryFindMatchingOpenParen(bodyText, leftIndex, out var openParenIndex))
                return false;

            var parenPrefixIndex = SkipWhitespaceBackward(bodyText, openParenIndex - 1);
            if (parenPrefixIndex < 0)
                return true;

            var parenPrefixChar = bodyText[parenPrefixIndex];
            if (parenPrefixChar is '.' or ']' or ')')
                return false;

            if (IsCSharpIdentifierPart(parenPrefixChar))
            {
                var parenIdentifierStart = parenPrefixIndex;
                while (parenIdentifierStart >= 0 && IsCSharpIdentifierPart(bodyText[parenIdentifierStart]))
                    parenIdentifierStart--;
                parenIdentifierStart++;

                var identifierPrefixIndex = SkipWhitespaceBackward(bodyText, parenIdentifierStart - 1);
                if (identifierPrefixIndex < 0)
                    return true;

                var identifierPrefixChar = bodyText[identifierPrefixIndex];
                if (identifierPrefixChar == '.')
                    return false;

                if (IsCSharpIdentifierPart(identifierPrefixChar))
                {
                    if (!TryReadPreviousIdentifierToken(bodyText, identifierPrefixIndex, out var identifierPreviousToken))
                        return false;

                    var normalizedPreviousToken = NormalizeCSharpIdentifier(identifierPreviousToken);
                    return normalizedPreviousToken is not ("when" or "is" or "as" or "and" or "or" or "not"
                        or "return" or "throw" or "new" or "case" or "else" or "do");
                }

                return identifierPrefixChar is '>' or ']' or ')' or '?' or ':' or '=';
            }

            return parenPrefixChar is '=' or '(' or ',' or ':';
        }

        var identifierEnd = leftIndex + 1;
        var identifierStart = leftIndex;
        while (identifierStart >= 0 && IsCSharpIdentifierPart(bodyText[identifierStart]))
            identifierStart--;
        identifierStart++;
        if (identifierStart >= identifierEnd || !IsCSharpIdentifierStart(bodyText[identifierStart]))
            return false;

        var prefixIndex = SkipWhitespaceBackward(bodyText, identifierStart - 1);
        if (prefixIndex < 0)
            return false;

        var prefixChar = bodyText[prefixIndex];
        return prefixChar is '=' or '(' or ',' or ':'
            || (TryReadPreviousIdentifierToken(bodyText, prefixIndex, out var previousToken)
                && (string.Equals(previousToken, "return", StringComparison.Ordinal)
                    || string.Equals(previousToken, "static", StringComparison.Ordinal)
                    || string.Equals(previousToken, "async", StringComparison.Ordinal)));
    }

    private static int GetLineStartOffset(string text, int offset)
    {
        var lineStart = Math.Min(offset, text.Length);
        while (lineStart > 0 && text[lineStart - 1] != '\n')
            lineStart--;
        return lineStart;
    }

    private static CSharpLineColumn GetLineColumnFromOffset(string text, int offset, int startLineNumber)
    {
        var lineNumber = startLineNumber;
        var column = 0;
        var limit = Math.Min(offset, text.Length);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n')
            {
                lineNumber++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return new CSharpLineColumn(lineNumber, column);
    }

    private static int SkipWhitespaceBackward(string text, int index)
    {
        while (index >= 0 && char.IsWhiteSpace(text[index]))
            index--;
        return index;
    }

    private static bool TryFindMatchingOpenParen(string text, int closeParenIndex, out int openParenIndex)
    {
        openParenIndex = -1;
        var depth = 0;
        for (var i = closeParenIndex; i >= 0; i--)
        {
            if (text[i] == ')')
            {
                depth++;
            }
            else if (text[i] == '(')
            {
                depth--;
                if (depth == 0)
                {
                    openParenIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadPreviousIdentifierToken(string text, int index, out string token)
    {
        token = string.Empty;
        var end = index;
        while (end >= 0 && !IsCSharpIdentifierPart(text[end]))
            end--;
        if (end < 0)
            return false;

        var start = end;
        while (start >= 0 && IsCSharpIdentifierPart(text[start]))
            start--;
        start++;
        if (start > end)
            return false;

        token = text[start..(end + 1)];
        return token.Length > 0;
    }

    private static bool IsStaticCSharpSymbol(SymbolRecord? symbol) =>
        symbol?.Signature != null && CSharpStaticModifierRegex.IsMatch(symbol.Signature);

    private static string GetFirstQualifiedSegment(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return string.Empty;

        var firstDot = qualifiedName.IndexOf('.');
        return firstDot < 0 ? qualifiedName : qualifiedName[..firstDot];
    }

    private static bool MatchesQualifiedConstantContainer(
        string qualifier,
        IReadOnlyList<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)> targets,
        bool allowShortNameFallback = true,
        bool allowSingleSegmentQualifiedMatch = false)
    {
        var hasMultipleQualifierSegments = qualifier.Contains('.') || qualifier.Contains("::", StringComparison.Ordinal);
        foreach (var (containerName, qualifiedContainerName, targetAllowsShortNameFallback) in targets)
        {
            if (!string.IsNullOrWhiteSpace(qualifiedContainerName)
                && ((hasMultipleQualifierSegments && QualifiedNameHasSuffix(qualifiedContainerName!, qualifier))
                    || (!hasMultipleQualifierSegments
                        && allowSingleSegmentQualifiedMatch
                        && string.Equals(qualifiedContainerName, qualifier, StringComparison.Ordinal))))
            {
                return true;
            }

            if (allowShortNameFallback
                && targetAllowsShortNameFallback
                && string.Equals(GetLastQualifiedSegment(qualifier), containerName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool QualifiedNameHasSuffix(string fullName, string suffix)
    {
        if (string.IsNullOrWhiteSpace(fullName) || string.IsNullOrWhiteSpace(suffix))
            return false;
        if (string.Equals(fullName, suffix, StringComparison.Ordinal))
            return true;
        if (suffix.Length >= fullName.Length)
            return false;

        var start = fullName.Length - suffix.Length;
        return string.Compare(fullName, start, suffix, 0, suffix.Length, StringComparison.Ordinal) == 0
            && fullName[start - 1] == '.';
    }

    private static string GetLastQualifiedSegment(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return string.Empty;

        var lastDot = qualifiedName.LastIndexOf('.');
        var lastColon = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        var split = Math.Max(lastDot, lastColon);
        return split < 0 ? qualifiedName : qualifiedName[(split + (split == lastColon ? 2 : 1))..];
    }
}
