using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool TryGetCSharpXmlDocCommentSpan(
        string line,
        bool inDelimitedDocComment,
        bool inOrdinaryBlockComment,
        out int commentStartIndex,
        out int commentEndExclusive,
        out bool nextDelimitedDocComment)
    {
        commentStartIndex = 0;
        commentEndExclusive = 0;
        nextDelimitedDocComment = inDelimitedDocComment;
        if (string.IsNullOrWhiteSpace(line))
        {
            commentEndExclusive = inDelimitedDocComment ? line.Length : 0;
            return inDelimitedDocComment;
        }

        var firstNonWhitespaceIndex = 0;
        while (firstNonWhitespaceIndex < line.Length && char.IsWhiteSpace(line[firstNonWhitespaceIndex]))
            firstNonWhitespaceIndex++;

        if (inDelimitedDocComment)
        {
            var closeIndex = line.IndexOf("*/", StringComparison.Ordinal);
            nextDelimitedDocComment = closeIndex < 0;
            commentStartIndex = 0;
            commentEndExclusive = closeIndex < 0 ? line.Length : closeIndex;
            return true;
        }

        if (inOrdinaryBlockComment)
            return false;

        if (line.AsSpan(firstNonWhitespaceIndex).StartsWith("///", StringComparison.Ordinal))
        {
            if (line.Length != firstNonWhitespaceIndex + 3 && line[firstNonWhitespaceIndex + 3] == '/')
                return false;

            commentStartIndex = firstNonWhitespaceIndex;
            commentEndExclusive = line.Length;
            return true;
        }

        if (!line.AsSpan(firstNonWhitespaceIndex).StartsWith("/**", StringComparison.Ordinal))
            return false;

        var closeAfterOpenIndex = line.IndexOf("*/", firstNonWhitespaceIndex + 3, StringComparison.Ordinal);
        nextDelimitedDocComment = closeAfterOpenIndex < 0;
        commentStartIndex = firstNonWhitespaceIndex;
        commentEndExclusive = closeAfterOpenIndex < 0 ? line.Length : closeAfterOpenIndex;
        return true;
    }

    private static bool HasCSharpValueReceiverConflict(
        string qualifier,
        string resolvedQualifier,
        int lineNumber,
        int column,
        SymbolRecord? callContainer,
        IReadOnlyDictionary<string, CSharpContainingTypeValueReceiverNames> valueReceiverNamesByContainingType,
        IReadOnlyDictionary<int, List<CSharpFunctionValueReceiverNameRecord>> valueReceiverNamesByFunctionStartLine)
    {
        if (string.IsNullOrWhiteSpace(qualifier)
            || (valueReceiverNamesByContainingType.Count == 0 && valueReceiverNamesByFunctionStartLine.Count == 0))
            return false;
        if (!string.Equals(qualifier, resolvedQualifier, StringComparison.Ordinal))
            return false;

        var receiverName = GetFirstQualifiedSegment(qualifier);
        if (string.IsNullOrWhiteSpace(receiverName))
            return false;

        if (callContainer != null
            && (callContainer.Kind == "function" || callContainer.Kind == "property")
            && valueReceiverNamesByFunctionStartLine.TryGetValue(callContainer.StartLine, out var functionNames)
            && HasCSharpFunctionValueReceiverName(functionNames, receiverName, lineNumber, column))
        {
            return true;
        }

        var containingType = GetContainingTypeQualifiedName(callContainer);
        return containingType != null
            && valueReceiverNamesByContainingType.TryGetValue(containingType, out var names)
            && (IsStaticCSharpSymbol(callContainer)
                ? names.StaticNames.Contains(receiverName)
                : names.StaticNames.Contains(receiverName) || names.InstanceNames.Contains(receiverName));
    }

    private static string? GetContainingTypeQualifiedName(SymbolRecord? symbol)
    {
        if (symbol == null)
            return null;
        if (IsTypeLikeSymbolKind(symbol.Kind))
            return CombineQualifiedName(symbol.ContainerQualifiedName, symbol.Name);
        return symbol.ContainerQualifiedName;
    }

    private static bool IsTypeLikeSymbolKind(string? kind) =>
        kind is "class" or "struct" or "interface";

    private static string? CombineQualifiedName(string? parentQualifiedName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (string.IsNullOrWhiteSpace(parentQualifiedName))
            return name;
        return $"{parentQualifiedName}.{name}";
    }

    private static bool IsWithinCSharpScope(CSharpFunctionValueReceiverNameRecord record, int lineNumber, int column)
    {
        var startsBefore = lineNumber > record.ScopeStartLine
            || (lineNumber == record.ScopeStartLine && column >= record.ScopeStartColumn);
        if (!startsBefore)
            return false;

        return lineNumber < record.ScopeEndLine
            || (lineNumber == record.ScopeEndLine && column < record.ScopeEndColumn);
    }

    private static void AddCSharpParameterNames(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string? signature,
        int scopeStartLine,
        int scopeStartColumn,
        int scopeEndLine,
        int scopeEndColumn,
        HashSet<CSharpFunctionValueReceiverNameRecord>? seenNames = null)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return;

        var openParen = signature.IndexOf('(');
        var closeParen = signature.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
            return;

        var parameters = signature[(openParen + 1)..closeParen];
        if (string.IsNullOrWhiteSpace(parameters))
            return;

        AddTopLevelCSharpParameterNames(
            names,
            parameters.AsSpan(),
            scopeStartLine,
            scopeStartColumn,
            scopeEndLine,
            scopeEndColumn,
            seenNames);
    }

    private static void AddTopLevelCSharpParameterNames(
        List<CSharpFunctionValueReceiverNameRecord> names,
        ReadOnlySpan<char> parameters,
        int scopeStartLine,
        int scopeStartColumn,
        int scopeEndLine,
        int scopeEndColumn,
        HashSet<CSharpFunctionValueReceiverNameRecord>? seenNames)
    {
        var depthAngle = 0;
        var depthParen = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        var segmentStart = 0;

        for (var i = 0; i < parameters.Length; i++)
        {
            var ch = parameters[i];
            switch (ch)
            {
                case '<':
                    depthAngle++;
                    break;
                case '>':
                    if (depthAngle > 0)
                        depthAngle--;
                    break;
                case '(':
                    depthParen++;
                    break;
                case ')':
                    if (depthParen > 0)
                        depthParen--;
                    break;
                case '[':
                    depthBracket++;
                    break;
                case ']':
                    if (depthBracket > 0)
                        depthBracket--;
                    break;
                case '{':
                    depthBrace++;
                    break;
                case '}':
                    if (depthBrace > 0)
                        depthBrace--;
                    break;
                case ',':
                    if (depthAngle == 0 && depthParen == 0 && depthBracket == 0 && depthBrace == 0)
                    {
                        AddCSharpParameterSegmentName(
                            names,
                            parameters[segmentStart..i],
                            scopeStartLine,
                            scopeStartColumn,
                            scopeEndLine,
                            scopeEndColumn,
                            seenNames);
                        segmentStart = i + 1;
                    }
                    break;
            }
        }

        if (segmentStart <= parameters.Length)
            AddCSharpParameterSegmentName(
                names,
                parameters[segmentStart..],
                scopeStartLine,
                scopeStartColumn,
                scopeEndLine,
                scopeEndColumn,
                seenNames);
    }

    private static void AddCSharpParameterSegmentName(
        List<CSharpFunctionValueReceiverNameRecord> names,
        ReadOnlySpan<char> segment,
        int scopeStartLine,
        int scopeStartColumn,
        int scopeEndLine,
        int scopeEndColumn,
        HashSet<CSharpFunctionValueReceiverNameRecord>? seenNames)
    {
        if (TryExtractTrailingCSharpParameterName(segment, out var name))
            AddCSharpFunctionValueReceiverName(names, name, scopeStartLine, scopeStartColumn, scopeEndLine, scopeEndColumn, seenNames);
    }

    private static bool TryExtractTrailingCSharpParameterName(ReadOnlySpan<char> segment, out string name)
    {
        name = string.Empty;
        var trimmed = segment.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("this".AsSpan(), StringComparison.Ordinal))
            return false;

        var end = trimmed.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(trimmed[end]))
            end--;
        while (end >= 0 && (trimmed[end] == '?' || trimmed[end] == '!'))
            end--;
        var start = end;
        while (start >= 0 && IsCSharpIdentifierPart(trimmed[start]))
            start--;
        if (end < 0 || start >= end)
            return false;

        name = NormalizeCSharpIdentifier(trimmed[(start + 1)..(end + 1)].ToString());
        return !string.IsNullOrWhiteSpace(name);
    }

    private static void AddCSharpLambdaParameterNames(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string bodyText,
        int startLineNumber,
        int scopeEndLine,
        HashSet<CSharpFunctionValueReceiverNameRecord>? seenNames = null)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return;

        var searchIndex = 0;
        while (searchIndex < bodyText.Length)
        {
            var arrowIndex = bodyText.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0)
                break;

            var lambdaScopeEnd = FindCSharpArrowExpressionScopeEndPosition(bodyText, arrowIndex, startLineNumber, scopeEndLine);
            AddCSharpLambdaParametersBeforeArrow(names, bodyText, arrowIndex, startLineNumber, lambdaScopeEnd, seenNames);
            searchIndex = arrowIndex + 2;
        }
    }

    private static void AddCSharpRecursivePatternValueReceiverNames(
        List<CSharpFunctionValueReceiverNameRecord> names,
        string bodyText,
        IReadOnlyList<string> structuralLines,
        int bodyStartIndex,
        int bodyEndIndex,
        HashSet<CSharpFunctionValueReceiverNameRecord>? seenNames = null)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return;

        var startLineNumber = bodyStartIndex + 1;
        foreach (var pattern in FindCSharpRecursivePatternValueNames(bodyText))
        {
            var position = GetLineColumnFromOffset(bodyText, pattern.Offset, startLineNumber);
            var declarationLineIndex = position.Line - 1;
            if (pattern.ArrowIndex >= 0)
            {
                var scopeEnd = FindCSharpArrowExpressionScopeEndPosition(bodyText, pattern.ArrowIndex, startLineNumber, bodyEndIndex + 1);
                AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, scopeEnd.Line, scopeEnd.Column, seenNames);
                continue;
            }

            if (pattern.IsCasePattern)
            {
                if (!TryFindCSharpSwitchCaseScopeEndPosition(structuralLines, bodyEndIndex, declarationLineIndex, position.Column, out var scopeEnd))
                    continue;

                AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, scopeEnd.Line, scopeEnd.Column, seenNames);
                continue;
            }

            if (!TryFindCSharpDeclarationPatternScopeEndPosition(structuralLines, bodyStartIndex, bodyEndIndex, declarationLineIndex, position.Column, out var declarationScopeEnd))
                continue;

            AddCSharpFunctionValueReceiverName(names, pattern.Name, position.Line, position.Column, declarationScopeEnd.Line, declarationScopeEnd.Column, seenNames);
        }
    }

    private static IEnumerable<CSharpRecursivePatternValueNameRecord> FindCSharpRecursivePatternValueNames(string bodyText)
    {
        for (var index = 0; index < bodyText.Length; index++)
        {
            if (!IsCSharpIdentifierStart(bodyText[index]))
                continue;

            var tokenStart = index;
            index++;
            while (index < bodyText.Length && IsCSharpIdentifierPart(bodyText[index]))
                index++;

            var token = bodyText[tokenStart..index];
            if ((string.Equals(token, "is", StringComparison.Ordinal) || string.Equals(token, "case", StringComparison.Ordinal))
                && TryParseCSharpRecursivePatternDesignation(bodyText, index, string.Equals(token, "case", StringComparison.Ordinal), out var name, out var designationOffset))
            {
                yield return new CSharpRecursivePatternValueNameRecord(name, designationOffset, string.Equals(token, "case", StringComparison.Ordinal));
            }

            index--;
        }

        foreach (var pattern in FindCSharpSwitchExpressionPatternValueNames(bodyText))
            yield return pattern;
    }

    private static IEnumerable<CSharpRecursivePatternValueNameRecord> FindCSharpSwitchExpressionPatternValueNames(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            yield break;

        for (var searchIndex = 0; searchIndex < bodyText.Length;)
        {
            var arrowIndex = bodyText.IndexOf("=>", searchIndex, StringComparison.Ordinal);
            if (arrowIndex < 0)
                yield break;

            searchIndex = arrowIndex + 2;
            if (IsPotentialCSharpLambdaArrow(bodyText, arrowIndex))
                continue;

            if (!TryFindCSharpSwitchExpressionArmStartOffset(bodyText, arrowIndex, out var armStartOffset))
                continue;

            if (!TryParseCSharpSwitchExpressionArmPatternDesignation(bodyText, armStartOffset, arrowIndex, out var name, out var designationOffset))
                continue;

            yield return new CSharpRecursivePatternValueNameRecord(name, designationOffset, false, arrowIndex);
        }
    }

    private static bool TryFindCSharpSwitchExpressionArmStartOffset(string bodyText, int arrowIndex, out int armStartOffset)
    {
        armStartOffset = 0;
        if (arrowIndex <= 0 || arrowIndex > bodyText.Length)
            return false;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = arrowIndex - 1; index >= 0; index--)
        {
            var current = bodyText[index];
            switch (current)
            {
                case ')':
                    parenDepth++;
                    break;
                case '(':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case ']':
                    bracketDepth++;
                    break;
                case '[':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '}':
                    braceDepth++;
                    break;
                case '{':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                        break;
                    }

                    if (parenDepth == 0 && bracketDepth == 0)
                    {
                        armStartOffset = SkipWhitespaceForward(bodyText, index + 1);
                        return armStartOffset < arrowIndex;
                    }

                    break;
                case ',':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        armStartOffset = SkipWhitespaceForward(bodyText, index + 1);
                        return armStartOffset < arrowIndex;
                    }

                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return false;
                    break;
            }
        }

        return false;
    }

    private static bool TryGetCSharpSwitchExpressionArmTypePatternRange(
        string bodyText,
        int arrowIndex,
        out int bodyStartOffset,
        out int armStartOffset,
        out int armPatternEndOffset)
    {
        bodyStartOffset = 0;
        armStartOffset = 0;
        armPatternEndOffset = 0;
        if (!TryFindCSharpSwitchExpressionBodyStartOffset(bodyText, arrowIndex, out bodyStartOffset))
            return false;

        var segmentStartOffset = bodyStartOffset + 1;
        if (segmentStartOffset >= arrowIndex)
            return false;

        var segmentText = bodyText[segmentStartOffset..arrowIndex];
        var lastCommaOffset = FindLastTopLevelCSharpComma(segmentText);
        var relativeArmStart = lastCommaOffset >= 0
            ? SkipWhitespaceForward(segmentText, lastCommaOffset + 1)
            : SkipWhitespaceForward(segmentText, 0);
        if (relativeArmStart >= segmentText.Length)
            return false;

        var armSegment = segmentText[relativeArmStart..];
        var whenOffset = FindTopLevelCSharpWhenKeywordOffset(armSegment);
        var relativePatternEnd = whenOffset >= 0
            ? relativeArmStart + whenOffset
            : segmentText.Length;
        while (relativePatternEnd > relativeArmStart && char.IsWhiteSpace(segmentText[relativePatternEnd - 1]))
            relativePatternEnd--;
        if (relativePatternEnd <= relativeArmStart)
            return false;

        armStartOffset = segmentStartOffset + relativeArmStart;
        armPatternEndOffset = segmentStartOffset + relativePatternEnd;
        return armStartOffset < armPatternEndOffset;
    }

    private static bool TryFindCSharpSwitchExpressionBodyStartOffset(string bodyText, int arrowIndex, out int bodyStartOffset)
    {
        bodyStartOffset = -1;
        if (arrowIndex <= 0 || arrowIndex > bodyText.Length)
            return false;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var index = arrowIndex - 1; index >= 0; index--)
        {
            var current = bodyText[index];
            switch (current)
            {
                case ')':
                    parenDepth++;
                    break;
                case '(':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case ']':
                    bracketDepth++;
                    break;
                case '[':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '}':
                    braceDepth++;
                    break;
                case '{':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                        break;
                    }

                    if (parenDepth == 0 && bracketDepth == 0)
                    {
                        bodyStartOffset = index;
                        return true;
                    }

                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        return false;
                    break;
            }
        }

        return false;
    }

    private static int FindLastTopLevelCSharpComma(string text)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var lastComma = -1;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
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
                case ',':
                    if (angleDepth == 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        lastComma = i;
                    break;
            }
        }

        return lastComma;
    }

    private static bool TryParseCSharpSwitchExpressionArmPatternDesignation(
        string bodyText,
        int armStartOffset,
        int arrowIndex,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        if (armStartOffset < 0 || armStartOffset >= arrowIndex || arrowIndex > bodyText.Length)
            return false;

        var preparedArmLines = StructuralLineMasker.MaskLines(
            "csharp",
            SplitCSharpSwitchExpressionArmLines(bodyText, armStartOffset, arrowIndex));
        for (var i = 0; i < preparedArmLines.Length; i++)
            preparedArmLines[i] = PrepareLine("csharp", preparedArmLines[i]);

        var preparedArmText = string.Join("\n", preparedArmLines);
        if (!TryParseCSharpRecursivePatternDesignation(preparedArmText, 0, false, out name, out var relativeOffset)
            && !TryParseCSharpSwitchExpressionArmDeclarationPatternDesignation(preparedArmText, out name, out relativeOffset))
        {
            return false;
        }

        designationOffset = armStartOffset + relativeOffset;
        return designationOffset < arrowIndex;
    }

    private static string[] SplitCSharpSwitchExpressionArmLines(string bodyText, int startOffset, int endOffset)
    {
        var length = endOffset - startOffset;
        var firstLineBreak = bodyText.IndexOf('\n', startOffset, length);
        if (firstLineBreak < 0)
            return [bodyText[startOffset..endOffset]];

        var lineCount = 2;
        for (var i = firstLineBreak + 1; i < endOffset; i++)
        {
            if (bodyText[i] == '\n')
                lineCount++;
        }

        var lines = new string[lineCount];
        var lineStart = startOffset;
        var lineIndex = 0;
        for (var i = startOffset; i < endOffset; i++)
        {
            if (bodyText[i] != '\n')
                continue;

            lines[lineIndex++] = bodyText[lineStart..i];
            lineStart = i + 1;
        }

        lines[lineIndex] = bodyText[lineStart..endOffset];
        return lines;
    }

    private static bool TryParseCSharpSwitchExpressionArmDeclarationPatternDesignation(
        string armText,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        if (string.IsNullOrWhiteSpace(armText))
            return false;

        var whenOffset = FindTopLevelCSharpWhenKeywordOffset(armText);
        var patternText = whenOffset >= 0 ? armText[..whenOffset] : armText;
        var match = CSharpSwitchExpressionDeclarationPatternValueNameRegex.Match(patternText);
        if (!match.Success)
            return false;

        name = NormalizeCSharpIdentifier(match.Groups["name"].Value);
        designationOffset = match.Groups["name"].Index;
        return designationOffset >= 0;
    }

    private static bool TryParseCSharpRecursivePatternDesignation(
        string bodyText,
        int index,
        bool isCasePattern,
        out string name,
        out int designationOffset)
    {
        name = string.Empty;
        designationOffset = -1;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var sawRecursiveClause = false;
        var previousTopLevelNonWhitespaceChar = '\0';
        for (var i = index; i < bodyText.Length; i++)
        {
            var current = bodyText[i];
            if (char.IsWhiteSpace(current))
                continue;

            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0 && IsCSharpIdentifierStart(current))
            {
                var tokenStart = i;
                i++;
                while (i < bodyText.Length && IsCSharpIdentifierPart(bodyText[i]))
                    i++;

                var token = bodyText[tokenStart..i];
                i--;
                if (sawRecursiveClause
                    && previousTopLevelNonWhitespaceChar is not '.' and not ':' and not '<' and not '[' and not '?'
                    && !IsCSharpPatternControlKeyword(token))
                {
                    name = NormalizeCSharpIdentifier(token);
                    designationOffset = tokenStart;
                    return true;
                }

                previousTopLevelNonWhitespaceChar = token[^1];
                continue;
            }

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
                    sawRecursiveClause = true;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
            }

            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                previousTopLevelNonWhitespaceChar = current;
        }

        return false;
    }

    private static bool IsCSharpPatternControlKeyword(string token) =>
        token is "and" or "or" or "not" or "when" or "null" or "true" or "false";

    private static int FindTopLevelCSharpWhenKeywordOffset(string text)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
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

            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && TryConsumeCSharpKeyword(text, i, "when", out _))
            {
                return i;
            }
        }

        return -1;
    }

}
