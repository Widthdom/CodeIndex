using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
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
        if (startLineIndex == endLineIndex)
        {
            var line = structuralLines[startLineIndex];
            var segmentStart = Math.Max(0, startColumn);
            var segmentEnd = Math.Min(endColumn, line.Length);
            return segmentStart < segmentEnd ? line.Substring(segmentStart, segmentEnd - segmentStart) : string.Empty;
        }

        var capacity = endLineIndex - startLineIndex;
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var segmentStart = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            var segmentEnd = lineIndex == endLineIndex ? Math.Min(endColumn, line.Length) : line.Length;
            if (segmentStart < segmentEnd)
                capacity += segmentEnd - segmentStart;
        }

        var builder = new System.Text.StringBuilder(capacity);
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
