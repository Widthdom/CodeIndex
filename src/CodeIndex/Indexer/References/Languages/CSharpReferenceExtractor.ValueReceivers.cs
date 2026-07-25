using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CSharpBlockScope(int StartLineIndex, int StartColumn, int EndLineIndex, int EndColumn);

    private static void AddCSharpLambdaParametersBeforeArrow(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string bodyText,
        int arrowIndex,
        int startLineNumber,
        CSharpLineColumn scopeEnd,
        HashSet<CSharpFunctionValueReceiverNameRecord>? seenNames = null)
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
            var parameters = bodyText.AsSpan(openParenIndex + 1, leftIndex - openParenIndex - 1);
            if (parameters.Trim().Length > 0)
                AddTopLevelCSharpParameterNames(
                    names,
                    parameters,
                    scopeStart.Line,
                    scopeStart.Column,
                    scopeEnd.Line,
                    scopeEnd.Column,
                    seenNames);

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
            AddCSharpFunctionValueReceiverName(names, parameter, declarationLine, identifierStart - GetLineStartOffset(bodyText, arrowIndex), scopeEnd.Line, scopeEnd.Column, seenNames);
        }
    }

    private static void AddCSharpFunctionValueReceiverName(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string name,
        int scopeStartLine,
        int scopeStartColumn,
        int scopeEndLine,
        int scopeEndColumn,
        HashSet<CSharpFunctionValueReceiverNameRecord>? seenNames = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        var record = new CSharpFunctionValueReceiverNameRecord(name, scopeStartLine, scopeStartColumn, scopeEndLine, scopeEndColumn);
        if (seenNames != null)
        {
            if (seenNames.Add(record))
                names.Add(record);
            return;
        }

        if (!names.Contains(record))
            names.Add(record);
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

    private static List<CSharpBlockScope> BuildCSharpBlockScopes(
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex)
    {
        var blockScopes = new List<CSharpBlockScope>();
        var stack = new Stack<(int LineIndex, int Column)>();
        for (var lineIndex = bodyStartIndex; lineIndex <= bodyEndIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                if (line[column] == '{')
                {
                    stack.Push((lineIndex, column));
                }
                else if (line[column] == '}' && stack.Count > 0)
                {
                    var start = stack.Pop();
                    blockScopes.Add(new CSharpBlockScope(start.LineIndex, start.Column, lineIndex, column));
                }
            }
        }

        return blockScopes;
    }

    private static int FindInnermostCSharpBlockEndLine(
        IReadOnlyList<CSharpBlockScope> blockScopes,
        int fallbackEndLine,
        int declarationLineIndex,
        int declarationColumn)
    {
        CSharpBlockScope? bestScope = null;
        foreach (var scope in blockScopes)
        {
            if (!ContainsCSharpBlockScope(scope, declarationLineIndex, declarationColumn))
                continue;

            if (bestScope == null || IsNarrowerCSharpBlockScope(scope, bestScope.Value))
            {
                bestScope = scope;
            }
        }

        return bestScope?.EndLineIndex + 1 ?? fallbackEndLine;
    }

    private static bool ContainsCSharpBlockScope(CSharpBlockScope scope, int lineIndex, int column)
    {
        var startsBefore = lineIndex > scope.StartLineIndex
            || (lineIndex == scope.StartLineIndex && column > scope.StartColumn);
        if (!startsBefore)
            return false;

        return lineIndex < scope.EndLineIndex
            || (lineIndex == scope.EndLineIndex && column < scope.EndColumn);
    }

    private static bool IsNarrowerCSharpBlockScope(CSharpBlockScope candidate, CSharpBlockScope current)
    {
        var candidateLineSpan = candidate.EndLineIndex - candidate.StartLineIndex;
        var currentLineSpan = current.EndLineIndex - current.StartLineIndex;
        if (candidateLineSpan != currentLineSpan)
            return candidateLineSpan < currentLineSpan;

        var candidateColumnSpan = candidate.EndColumn - candidate.StartColumn;
        var currentColumnSpan = current.EndColumn - current.StartColumn;
        return candidateColumnSpan < currentColumnSpan;
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

        var bodyText = LineRangeText.Join(structuralLines, bodyStartIndex, bodyEndIndex);
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

}
