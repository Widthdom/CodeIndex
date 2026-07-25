using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static void EmitCatchTypeReferences(
        string language,
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (language is not ("csharp" or "java" or "kotlin"))
            return;

        var catchIndex = FindTopLevelKeyword(line, "catch");
        if (catchIndex < 0)
            return;

        var openParen = line.IndexOf('(', catchIndex + "catch".Length);
        if (openParen < 0)
            return;

        var closeParen = FindMatchingChar(line, openParen, '(', ')');
        if (closeParen < 0 || closeParen <= openParen + 1)
            return;

        var clauseStart = openParen + 1;
        var clause = line.Substring(clauseStart, closeParen - clauseStart);
        if (language == "kotlin")
        {
            EmitKotlinCatchTypeReference(
                clause,
                clauseStart,
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            return;
        }

        EmitCStyleCatchTypeReferences(
            language,
            clause,
            clauseStart,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitKotlinCatchTypeReference(
        string clause,
        int clauseStartInLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(clause, ':');
        if (colonIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(clause, colonIndex + 1);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(clause, typeStart);
        if (typeEnd <= typeStart)
            return;

        var absoluteStart = clauseStartInLine + typeStart;
        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            clause.Substring(typeStart, typeEnd - typeStart),
            absoluteStart,
            context,
            lineNumber,
            resolveContainerForColumn(absoluteStart),
            "kotlin");
    }

    private static void EmitCStyleCatchTypeReferences(
        string language,
        string clause,
        int clauseStartInLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var start = SkipCatchParameterPrefix(language, clause, 0);
        var end = clause.Length;
        while (end > start && char.IsWhiteSpace(clause[end - 1]))
            end--;
        if (end <= start)
            return;

        var typeEnd = FindCatchTypeEndBeforeVariable(language, clause, start, end);
        if (typeEnd <= start)
            return;

        var typeExpression = clause.AsSpan(start, typeEnd - start);
        foreach (var (segmentStart, segmentLength) in SplitTopLevelPipeSpans(typeExpression))
        {
            var leading = CountLeadingWhitespace(typeExpression, segmentStart, segmentLength);
            var trimmedLength = segmentLength - leading;
            while (trimmedLength > 0 && char.IsWhiteSpace(typeExpression[segmentStart + leading + trimmedLength - 1]))
                trimmedLength--;
            if (trimmedLength <= 0)
                continue;

            var absoluteStart = clauseStartInLine + start + segmentStart + leading;
            AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                typeExpression.Slice(segmentStart + leading, trimmedLength).ToString(),
                absoluteStart,
                context,
                lineNumber,
                resolveContainerForColumn(absoluteStart),
                language);
        }
    }

    private static int SkipCatchParameterPrefix(string language, string clause, int start)
    {
        var i = start;
        while (i < clause.Length)
        {
            while (i < clause.Length && char.IsWhiteSpace(clause[i]))
                i++;

            if (language == "java" && i < clause.Length && clause[i] == '@')
            {
                i = SkipJavaAnnotation(clause, i) + 1;
                continue;
            }

            if (language == "java" && IsWordAt(clause, i, "final"))
            {
                i += "final".Length;
                continue;
            }

            break;
        }

        return i;
    }

    private static int FindCatchTypeEndBeforeVariable(string language, string clause, int start, int end)
    {
        if (!TryFindLastIdentifier(clause, start, end, out var lastStart, out _))
            return end;

        var before = clause.AsSpan(start, lastStart - start).TrimEnd();
        if (language == "csharp" && before.EndsWith("@", StringComparison.Ordinal))
        {
            var prefix = before[..^1].TrimEnd();
            if (prefix.Length == 0
                || prefix.EndsWith(".", StringComparison.Ordinal)
                || prefix.EndsWith("::", StringComparison.Ordinal))
            {
                return end;
            }

            return lastStart - 1;
        }

        if (before.Length == 0
            || before.EndsWith(".", StringComparison.Ordinal)
            || before.EndsWith("::", StringComparison.Ordinal))
        {
            return end;
        }

        return lastStart;
    }

    private static bool TryFindLastIdentifier(string text, int start, int end, out int identifierStart, out int identifierEnd)
    {
        identifierStart = -1;
        identifierEnd = -1;
        var i = end - 1;
        while (i >= start && char.IsWhiteSpace(text[i]))
            i--;
        if (i < start || !IsJavaIdentifierPart(text[i]))
            return false;

        identifierEnd = i + 1;
        while (i >= start && IsJavaIdentifierPart(text[i]))
            i--;
        identifierStart = i + 1;
        return identifierStart < identifierEnd;
    }

    private static List<(int Start, int Length)> SplitTopLevelPipeSpans(string text) => SplitTopLevelPipeSpans(text.AsSpan());

    private static List<(int Start, int Length)> SplitTopLevelPipeSpans(ReadOnlySpan<char> text)
    {
        if (text.IndexOf('|') < 0)
            return [(0, text.Length)];

        var spans = new List<(int Start, int Length)>(4);
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        var start = 0;
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
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '|' when angleDepth == 0 && parenDepth == 0 && squareDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    private static bool IsWordAt(string text, int index, string word)
    {
        if (index + word.Length > text.Length)
            return false;
        if (string.CompareOrdinal(text, index, word, 0, word.Length) != 0)
            return false;
        if (index > 0 && IsJavaIdentifierPart(text[index - 1]))
            return false;
        var after = index + word.Length;
        return after >= text.Length || !IsJavaIdentifierPart(text[after]);
    }

    private static bool TryFindFirstTopLevelCSharpArrow(string text, out int arrowIndex)
    {
        arrowIndex = -1;
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        var braceDepth = 0;
        for (var i = 0; i + 1 < text.Length; i++)
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
                case '=':
                    if (text[i + 1] == '>'
                        && angleDepth == 0
                        && parenDepth == 0
                        && squareDepth == 0
                        && braceDepth == 0)
                    {
                        arrowIndex = i;
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool IsRustLifetimeStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsRustLifetimePart(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static bool IsSwiftTupleElementLabelSegment(string expression, int segmentStart, int segmentEnd)
    {
        var next = segmentEnd;
        while (next < expression.Length && char.IsWhiteSpace(expression[next]))
            next++;
        if (next >= expression.Length || expression[next] != ':')
            return false;
        if (next + 1 < expression.Length && expression[next + 1] == ':')
            return false;

        var previous = segmentStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(expression[previous]))
            previous--;

        return previous >= 0 && expression[previous] is '(' or ',';
    }

    private static bool IsSwiftMetatypeSuffixSegment(string expression, int segmentStart, string segment)
    {
        if (segment is not ("Type" or "Protocol"))
            return false;

        var previous = segmentStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(expression[previous]))
            previous--;

        return previous >= 0 && expression[previous] == '.';
    }

    private static void AddTypeScriptTypeExpressionSegments(
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
        ignoredSegments ??= TypeScriptTypeExpressionIgnoredSegments;

        int i = 0;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(expression, i);
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    expression,
                    i,
                    expressionStartInLine,
                    context,
                    lineNumber,
                    references,
                    seen,
                    fileId,
                    container,
                    ignoredSegments);
                continue;
            }

            if (!IsJavaIdentifierStart(c))
            {
                i++;
                continue;
            }

            int segmentStart = i;
            i++;
            while (i < expression.Length && IsJavaIdentifierPart(expression[i]))
                i++;

            var segment = expression.Substring(segmentStart, i - segmentStart);
            if (TypeScriptTypeExpressionIgnoredSegments.Contains(segment)
                || ignoredSegments != null && ignoredSegments.Contains(segment))
            {
                continue;
            }
            if (IsTypeScriptTypeLabelSegment(expression, i))
                continue;

            AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                segment,
                expressionStartInLine + segmentStart,
                context,
                lineNumber,
                container,
                "typescript",
                ignoredSegments: ignoredSegments);
        }
    }

    private static bool IsTypeScriptTypeLabelSegment(string expression, int segmentEnd)
    {
        var next = segmentEnd;
        while (next < expression.Length && char.IsWhiteSpace(expression[next]))
            next++;

        return next < expression.Length
            && expression[next] == ':'
            && (next + 1 >= expression.Length || expression[next + 1] != ':');
    }

    private static int ScanTypeScriptTemplateLiteralForTypeExpression(
        string expression,
        int startIndex,
        int expressionStartInLine,
        string context,
        int lineNumber,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container,
        IReadOnlySet<string>? ignoredSegments)
    {
        int i = startIndex + 1;
        while (i < expression.Length)
        {
            char c = expression[i];
            if (c == '\\')
            {
                i += Math.Min(2, expression.Length - i);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(expression, i);
                continue;
            }

            if (c == '$' && i + 1 < expression.Length && expression[i + 1] == '{')
            {
                int holeStart = i + 2;
                int holeEnd = FindMatchingTypeScriptHoleEndForTypeExpression(expression, holeStart);
                if (holeEnd < 0)
                    return expression.Length;

                AddTypeScriptTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    expression.Substring(holeStart, holeEnd - holeStart),
                    expressionStartInLine + holeStart,
                    context,
                    lineNumber,
                    container,
                    ignoredSegments);
                i = holeEnd + 1;
                continue;
            }

            if (c == '`')
                return i + 1;

            i++;
        }

        return expression.Length;
    }

    private static int FindMatchingTypeScriptHoleEndForTypeExpression(string text, int startIndex)
    {
        int braceDepth = 0;
        int i = startIndex;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += Math.Min(2, text.Length - i);
                continue;
            }

            if (c == '\'' || c == '"')
            {
                i = SkipTypeScriptStringLiteral(text, i);
                continue;
            }

            if (c == '`')
            {
                i = ScanTypeScriptTemplateLiteralForTypeExpression(
                    text,
                    i,
                    0,
                    string.Empty,
                    0,
                    [],
                    new ReferenceDedupeSet(),
                    0,
                    null,
                    null);
                continue;
            }

            if (c == '{')
            {
                braceDepth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (braceDepth == 0)
                    return i;
                braceDepth--;
                i++;
                continue;
            }

            i++;
        }

        return -1;
    }

    private static int SkipTypeScriptStringLiteral(string text, int startIndex)
    {
        char quote = text[startIndex];
        int i = startIndex + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += Math.Min(2, text.Length - i);
                continue;
            }

            if (text[i] == quote)
                return i + 1;

            i++;
        }

        return text.Length;
    }

    private static int SkipTypeScriptBlockCommentForTypeExpression(string text, int startIndex)
    {
        for (int i = startIndex; i + 1 < text.Length; i++)
        {
            if (text[i] == '*' && text[i + 1] == '/')
                return i + 1;
        }

        return text.Length - 1;
    }

    private static int SkipBalanced(string line, int start, char open, char close)
    {
        int depth = 0;
        int i = start;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == open)
                depth++;
            else if (c == close)
            {
                depth--;
                if (depth <= 0)
                    return i + 1;
            }
            i++;
        }
        return i;
    }

}
