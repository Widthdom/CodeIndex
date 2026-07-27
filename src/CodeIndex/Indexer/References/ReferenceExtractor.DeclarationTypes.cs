using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static void EmitDeclarationTypeReferences(
        string language,
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (TryFindCallableParameterList(line, language, out var callableNameStart, out var paramStart, out var paramEnd))
        {
            if (TryGetCallableReturnTypeSpan(line, callableNameStart, language, out var typeStart, out var typeLength))
            {
                AddTypeExpressionSegmentsForLanguage(
                    language,
                    references,
                    seen,
                    fileId,
                    line.Substring(typeStart, typeLength),
                    typeStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(typeStart),
                    ignoredSegments);
            }

            EmitParameterTypeReferences(
                language,
                line,
                paramStart,
                paramEnd,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                ignoredSegments);
        }

        if (TryGetSimpleDeclarationTypeSpan(line, language, out var declarationTypeStart, out var declarationTypeLength))
        {
            AddTypeExpressionSegmentsForLanguage(
                language,
                references,
                seen,
                fileId,
                line.Substring(declarationTypeStart, declarationTypeLength),
                declarationTypeStart,
                context,
                lineNumber,
                resolveContainerForColumn(declarationTypeStart),
                ignoredSegments);
        }

        EmitCSharpModifierContinuationTypeReferences(
            language,
            line,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ignoredSegments);
    }

    internal static void EmitTypeScriptDeclarationTypeReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        int equalsIndex = FindTopLevelAssignmentIndex(line);
        if (equalsIndex < 0)
            return;

        var head = line.Substring(0, equalsIndex);
        var tokens = GetTopLevelTokenSpans(head);
        if (tokens.Count < 2)
            return;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = head.Substring(tokens[first].Start, tokens[first].Length);
            if (token is "export" or "declare")
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return;

        var keyword = head.Substring(tokens[first].Start, tokens[first].Length);
        if (!string.Equals(keyword, "type", StringComparison.Ordinal))
            return;

        int typeStart = SkipWhitespace(line, equalsIndex + 1);
        if (typeStart >= line.Length)
            return;

        int typeEnd = FindTypeScriptTypeExpressionTerminator(line, typeStart);
        if (typeEnd < 0)
            typeEnd = line.Length;

        AddTypeScriptTypeExpressionSegments(
            references,
            seen,
            fileId,
            line.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static int FindTypeScriptTypeExpressionTerminator(string line, int startIndex)
    {
        int angleDepth = 0;
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;

        for (int i = startIndex; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(line, i) - 1;
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    line,
                    i,
                    0,
                    string.Empty,
                    0,
                    [],
                    new ReferenceDedupeSet(),
                    0,
                    null,
                    null) - 1;
                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                    return i;
                if (line[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockCommentForTypeExpression(line, i + 2);
                    continue;
                }
            }

            switch (c)
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
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case ';' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    return i;
            }
        }

        return line.Length;
    }

    internal static bool TryFindCallableParameterList(
        string line,
        string language,
        out int callableNameStart,
        out int paramStart,
        out int paramEnd)
    {
        callableNameStart = -1;
        paramStart = -1;
        paramEnd = -1;

        if (IsDefinitelyNotTypeDeclarationLine(line, language))
            return false;

        int openParen = FindFirstTopLevelChar(line, '(');
        if (openParen <= 0)
            return false;
        if (!TryFindCallableName(line, openParen, language, out callableNameStart))
            return false;

        int closeParen = FindMatchingChar(line, openParen, '(', ')');
        if (closeParen < 0)
            return false;

        paramStart = openParen + 1;
        paramEnd = closeParen;
        return true;
    }

    private static bool TryFindCallableName(string line, int openParen, string language, out int nameStart)
    {
        nameStart = -1;
        int i = openParen - 1;
        while (i >= 0 && char.IsWhiteSpace(line[i]))
            i--;
        if (i < 0)
            return false;

        if (line[i] == '>')
        {
            int depth = 1;
            i--;
            while (i >= 0 && depth > 0)
            {
                if (line[i] == '>')
                    depth++;
                else if (line[i] == '<')
                    depth--;
                i--;
            }
            while (i >= 0 && char.IsWhiteSpace(line[i]))
                i--;
        }

        if (i < 0 || !IsTypeExpressionIdentifierPart(language, line[i]))
            return false;
        int end = i + 1;
        while (i >= 0 && IsTypeExpressionIdentifierPart(language, line[i]))
            i--;
        nameStart = i + 1;

        var name = line.Substring(nameStart, end - nameStart);
        if (IsIgnoredCallName(language, name))
            return false;
        return true;
    }

    internal static bool TryGetCallableReturnTypeSpan(string line, int callableNameStart, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;
        var prefix = line.Substring(0, callableNameStart);
        if (prefix.IndexOf('=') >= 0 || prefix.Contains("=>", StringComparison.Ordinal))
            return false;

        var tokens = GetTopLevelTokenSpans(prefix);
        if (tokens.Count == 0)
            return false;

        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var token = prefix.Substring(tokens[i].Start, tokens[i].Length);
            if (IsCallablePrefixModifier(language, token) || token.StartsWith("[", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal))
                continue;
            if (!HasWhitespaceGap(prefix, tokens[i].Start + tokens[i].Length))
                return false;
            typeStart = tokens[i].Start;
            typeLength = tokens[i].Length;
            return true;
        }

        return false;
    }

    private static void EmitParameterTypeReferences(
        string language,
        string line,
        int paramStart,
        int paramEnd,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (paramEnd <= paramStart)
            return;

        var parameterList = line.AsSpan(paramStart, paramEnd - paramStart);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelCommaSpans(parameterList))
        {
            var fragment = parameterList.Slice(segmentStart, segmentLength).ToString();
            if (!TryGetParameterTypeRelativeSpan(fragment, language, out var typeRelativeStart, out var typeRelativeLength))
                continue;

            int absoluteStart = paramStart + segmentStart + typeRelativeStart;
            AddTypeExpressionSegmentsForLanguage(
                language,
                references,
                seen,
                fileId,
                fragment.Substring(typeRelativeStart, typeRelativeLength),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                ignoredSegments);
        }
    }

    private static void AddTypeExpressionSegmentsForLanguage(
        string language,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (language == "typescript")
        {
            AddTypeScriptTypeExpressionSegments(
                references,
                seen,
                fileId,
                expression,
                expressionStartInLine,
                context,
                lineNumber,
                container);
            return;
        }

        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            expression,
            expressionStartInLine,
            context,
            lineNumber,
            container,
            language,
            ignoredSegments: ignoredSegments);
    }

    internal static void AddTypeScriptTypeExpressionSegments(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (TypedLanguageReferenceExtractor.TryEmitTypeScriptFunctionTypeExpressionReferences(
                expression,
                expressionStartInLine,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                container))
        {
            return;
        }

        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];

            if (c is '\'' or '"')
            {
                i = SkipTypeScriptQuotedString(expression, i);
                continue;
            }

            if (c == '`')
            {
                i = SkipTypeScriptTemplateLiteral(expression, i, references, seen, fileId, expressionStartInLine, context, lineNumber, container);
                continue;
            }

            if (c == '/' && i + 1 < expression.Length)
            {
                if (expression[i + 1] == '/')
                {
                    i = SkipTypeScriptLineComment(expression, i + 2);
                    continue;
                }

                if (expression[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockCommentForTypeExpression(expression, i + 2);
                    continue;
                }
            }

            if (!IsTypeExpressionIdentifierStart("typescript", c))
                continue;

            int segmentStart = i;
            while (i < expression.Length && IsTypeExpressionIdentifierPart("typescript", expression[i]))
                i++;

            var segment = expression.Substring(segmentStart, i - segmentStart);
            if (TypeScriptTypeExpressionIgnoredSegments.Contains(segment))
            {
                i--;
                continue;
            }

            AddTypeReferenceSegment(references, seen, fileId, segment, expressionStartInLine + segmentStart, context, lineNumber, container, "typescript");
            i--;
        }
    }

    private static int SkipTypeScriptQuotedString(string text, int start)
    {
        char quote = text[start];
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (text[i] == quote)
                return i;

            i++;
        }

        return text.Length - 1;
    }

    private static int SkipTypeScriptLineComment(string text, int start)
    {
        int i = start;
        while (i < text.Length && text[i] != '\n' && text[i] != '\r')
            i++;
        return Math.Max(start - 1, i - 1);
    }

    private static int SkipTypeScriptBlockComment(string text, int start)
    {
        for (int i = start; i + 1 < text.Length; i++)
        {
            if (text[i] == '*' && text[i + 1] == '/')
                return i + 1;
        }

        return text.Length - 1;
    }

    private static int SkipTypeScriptTemplateLiteral(
        string text,
        int start,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (c == '`')
                return i;

            if (c == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                int holeStart = i + 2;
                int holeEnd = FindMatchingTypeScriptTemplateHoleEnd(text, holeStart);
                if (holeEnd < 0)
                    return text.Length - 1;

                var hole = text.Substring(holeStart, holeEnd - holeStart);
                AddTypeScriptTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    hole,
                    expressionStartInLine + holeStart,
                    context,
                    lineNumber,
                    container);
                i = holeEnd + 1;
                continue;
            }

            i++;
        }

        return text.Length - 1;
    }

    private static int SkipTypeScriptTemplateLiteralForMatching(string text, int start)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (c == '`')
                return i;

            if (c == '$' && i + 1 < text.Length && text[i + 1] == '{')
            {
                int holeEnd = FindMatchingTypeScriptTemplateHoleEnd(text, i + 2);
                if (holeEnd < 0)
                    return text.Length - 1;

                i = holeEnd + 1;
                continue;
            }

            i++;
        }

        return text.Length - 1;
    }

    private static int FindMatchingTypeScriptTemplateHoleEnd(string text, int start)
    {
        int braceDepth = 1;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (c is '\'' or '"')
            {
                i = SkipTypeScriptQuotedString(text, i);
                continue;
            }

            if (c == '`')
            {
                i = SkipTypeScriptTemplateLiteralForMatching(text, i);
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    i = SkipTypeScriptLineComment(text, i + 2);
                    continue;
                }

                if (text[i + 1] == '*')
                {
                    i = SkipTypeScriptBlockComment(text, i + 2);
                    continue;
                }
            }

            if (c == '{')
            {
                braceDepth++;
                continue;
            }

            if (c == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static bool TryGetParameterTypeRelativeSpan(string parameterFragment, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;

        int end = FindTopLevelAssignmentIndex(parameterFragment);
        if (end < 0)
            end = parameterFragment.Length;
        var candidate = parameterFragment.Substring(0, end);
        var tokens = GetTopLevelTokenSpans(candidate);
        if (tokens.Count < 2)
            return false;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = candidate.Substring(tokens[first].Start, tokens[first].Length);
            if (token.StartsWith("[", StringComparison.Ordinal) || token.StartsWith("@", StringComparison.Ordinal) || IsParameterModifier(language, token))
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return false;

        typeStart = tokens[first].Start;
        int lastTypeToken = tokens.Count - 2;
        while (lastTypeToken >= first)
        {
            var token = candidate.Substring(tokens[lastTypeToken].Start, tokens[lastTypeToken].Length);
            if (IsParameterModifier(language, token))
            {
                lastTypeToken--;
                continue;
            }

            break;
        }

        if (lastTypeToken < first)
            return false;
        typeLength = tokens[lastTypeToken].Start + tokens[lastTypeToken].Length - typeStart;
        return true;
    }

    private static void EmitCSharpModifierContinuationTypeReferences(
        string language,
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        if (language != "csharp")
            return;

        var fragments = SplitTopLevelCommaSpans(line);
        if (fragments.Count <= 1)
            return;

        foreach (var (fragmentStart, fragmentLength) in fragments)
        {
            var fragment = line.Substring(fragmentStart, fragmentLength);
            var tokens = GetTopLevelTokenSpans(fragment);
            int first = 0;
            while (first < tokens.Count)
            {
                var token = fragment.Substring(tokens[first].Start, tokens[first].Length);
                if (!token.StartsWith("[", StringComparison.Ordinal))
                    break;
                first++;
            }

            if (first >= tokens.Count)
                continue;

            var firstToken = fragment.Substring(tokens[first].Start, tokens[first].Length);
            if (!IsParameterModifier(language, firstToken)
                || !TryGetParameterTypeRelativeSpan(fragment, language, out var typeRelativeStart, out var typeRelativeLength))
            {
                continue;
            }

            int absoluteStart = fragmentStart + typeRelativeStart;
            AddTypeExpressionSegmentsForLanguage(
                language,
                references,
                seen,
                fileId,
                fragment.Substring(typeRelativeStart, typeRelativeLength),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                ignoredSegments);
        }
    }

    private static bool TryGetSimpleDeclarationTypeSpan(string line, string language, out int typeStart, out int typeLength)
    {
        typeStart = -1;
        typeLength = 0;

        if (IsDefinitelyNotTypeDeclarationLine(line, language))
            return false;

        int firstParen = FindFirstTopLevelChar(line, '(');
        int firstTerminator = FindFirstTopLevelChar(line, ';');
        int firstBrace = FindFirstTopLevelChar(line, '{');
        int firstEquals = FindFirstTopLevelChar(line, '=');
        int firstComma = FindFirstTopLevelChar(line, ',');
        int boundary = int.MaxValue;
        if (firstTerminator >= 0) boundary = Math.Min(boundary, firstTerminator);
        if (firstBrace >= 0) boundary = Math.Min(boundary, firstBrace);
        if (firstEquals >= 0) boundary = Math.Min(boundary, firstEquals);
        if (firstComma >= 0) boundary = Math.Min(boundary, firstComma);
        if (boundary == int.MaxValue)
            return false;
        if (firstParen >= 0 && firstParen < boundary)
            return false;

        var head = line.Substring(0, boundary);
        var tokens = GetTopLevelTokenSpans(head);
        if (tokens.Count < 2)
            return false;

        int first = 0;
        while (first < tokens.Count)
        {
            var token = head.Substring(tokens[first].Start, tokens[first].Length);
            if (token.StartsWith("[", StringComparison.Ordinal)
                || token.StartsWith("@", StringComparison.Ordinal)
                || IsDeclarationModifier(language, token)
                || IsParameterModifier(language, token))
            {
                first++;
                continue;
            }

            break;
        }

        if (first >= tokens.Count - 1)
            return false;

        var declaredNameToken = head.Substring(tokens[^1].Start, tokens[^1].Length);
        if (!IsSimpleDeclarationIdentifier(language, declaredNameToken))
            return false;

        typeStart = tokens[first].Start;
        int lastTypeToken = tokens.Count - 2;
        typeLength = tokens[lastTypeToken].Start + tokens[lastTypeToken].Length - typeStart;
        return true;
    }

    private static bool IsDefinitelyNotTypeDeclarationLine(string line, string language)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
            return true;
        if (language == "csharp"
            && TryFindFirstTopLevelCSharpArrow(line, out var arrowIndex))
        {
            var commaIndex = FindFirstTopLevelChar(line, ',');
            var semicolonIndex = FindFirstTopLevelChar(line, ';');
            if (commaIndex > arrowIndex && (semicolonIndex < 0 || commaIndex < semicolonIndex))
                return true;
        }

        if (trimmed.StartsWith("using ", StringComparison.Ordinal)
            || trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || trimmed.StartsWith("package ", StringComparison.Ordinal)
            || trimmed.StartsWith("import ", StringComparison.Ordinal)
            || trimmed.StartsWith("return ", StringComparison.Ordinal)
            || trimmed.StartsWith("throw ", StringComparison.Ordinal)
            || trimmed.StartsWith("if ", StringComparison.Ordinal)
            || trimmed.StartsWith("if(", StringComparison.Ordinal)
            || trimmed.StartsWith("switch ", StringComparison.Ordinal)
            || trimmed.StartsWith("switch(", StringComparison.Ordinal)
            || trimmed.StartsWith("while ", StringComparison.Ordinal)
            || trimmed.StartsWith("while(", StringComparison.Ordinal)
            || trimmed.StartsWith("for ", StringComparison.Ordinal)
            || trimmed.StartsWith("for(", StringComparison.Ordinal)
            || trimmed.StartsWith("foreach ", StringComparison.Ordinal)
            || trimmed.StartsWith("foreach(", StringComparison.Ordinal)
            || trimmed.StartsWith("catch ", StringComparison.Ordinal)
            || trimmed.StartsWith("catch(", StringComparison.Ordinal)
            || trimmed.StartsWith("lock ", StringComparison.Ordinal)
            || trimmed.StartsWith("lock(", StringComparison.Ordinal)
            || trimmed.StartsWith("case ", StringComparison.Ordinal)
            || trimmed.StartsWith("else", StringComparison.Ordinal)
            || trimmed.StartsWith("do", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.StartsWith("class ", StringComparison.Ordinal)
            || trimmed.StartsWith("struct ", StringComparison.Ordinal)
            || trimmed.StartsWith("interface ", StringComparison.Ordinal)
            || trimmed.StartsWith("record ", StringComparison.Ordinal)
            || (language == "java" && trimmed.StartsWith("enum ", StringComparison.Ordinal));
    }

}
