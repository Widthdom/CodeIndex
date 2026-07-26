using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    internal static void EmitGoBranchLabelReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references)
    {
        foreach (Match match in Regex.EnumerateMatches(
                     GoBranchLabelRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                return;

            addCallLikeReference(match.Groups["name"].Value, match.Groups["name"].Index);
        }
    }

    private static void EmitGoFunctionSignatureTypes(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var firstParen = preparedLine.IndexOf('(');
        if (firstParen < 0)
            return;

        var parameterOpen = firstParen;
        var functionHeaderStart = GoFuncRegex.Match(preparedLine).Length;
        var receiverClose = ReferenceExtractor.FindMatchingChar(preparedLine, firstParen, '(', ')');
        if (receiverClose >= 0)
        {
            var afterReceiver = receiverClose + 1;
            while (afterReceiver < preparedLine.Length && char.IsWhiteSpace(preparedLine[afterReceiver]))
                afterReceiver++;
            if (afterReceiver < preparedLine.Length && IsIdentifierStart(preparedLine[afterReceiver]))
            {
                var nextParen = preparedLine.IndexOf('(', afterReceiver);
                if (nextParen > afterReceiver)
                {
                    EmitGoParameterListTypes(preparedLine, firstParen + 1, receiverClose, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                    functionHeaderStart = afterReceiver;

                    var afterName = afterReceiver + 1;
                    while (afterName < preparedLine.Length && IsSimpleIdentifierPart(preparedLine[afterName]))
                        afterName++;
                    while (afterName < preparedLine.Length && char.IsWhiteSpace(preparedLine[afterName]))
                        afterName++;

                    if (afterName < preparedLine.Length && preparedLine[afterName] == '[')
                    {
                        var typeParameterClose = ReferenceExtractor.FindMatchingChar(preparedLine, afterName, '[', ']');
                        if (typeParameterClose > afterName)
                        {
                            EmitGoTypeParameterConstraints(preparedLine, afterName, typeParameterClose + 1, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                            var valueParameterOpen = preparedLine.IndexOf('(', typeParameterClose + 1);
                            if (valueParameterOpen < 0)
                                return;

                            nextParen = valueParameterOpen;
                        }
                    }

                    parameterOpen = nextParen;
                }
            }
        }

        var parameterClose = ReferenceExtractor.FindMatchingChar(preparedLine, parameterOpen, '(', ')');
        if (parameterClose < 0)
            return;

        EmitGoTypeParameterConstraints(preparedLine, functionHeaderStart, parameterOpen, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoParameterListTypes(preparedLine, parameterOpen + 1, parameterClose, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        var returnStart = parameterClose + 1;
        while (returnStart < preparedLine.Length && char.IsWhiteSpace(preparedLine[returnStart]))
            returnStart++;
        if (returnStart >= preparedLine.Length || preparedLine[returnStart] == '{')
            return;

        if (preparedLine[returnStart] == '(')
        {
            var returnClose = ReferenceExtractor.FindMatchingChar(preparedLine, returnStart, '(', ')');
            if (returnClose > returnStart)
                EmitGoParameterListTypes(preparedLine, returnStart + 1, returnClose, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            return;
        }

        var returnEnd = returnStart;
        while (returnEnd < preparedLine.Length && preparedLine[returnEnd] != '{')
            returnEnd++;

        EmitGoTypeExpression(preparedLine, returnStart, returnEnd, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoInterfaceMethodSignatureTypes(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var nameStart = SkipWhitespace(preparedLine, 0);
        if (nameStart >= preparedLine.Length || !IsIdentifierStart(preparedLine[nameStart]))
            return;

        var nameEnd = nameStart + 1;
        while (nameEnd < preparedLine.Length && IsSimpleIdentifierPart(preparedLine[nameEnd]))
            nameEnd++;

        if (IsGoStatementKeyword(preparedLine[nameStart..nameEnd]))
            return;

        var open = SkipWhitespace(preparedLine, nameEnd);
        if (open >= preparedLine.Length || preparedLine[open] != '(')
            return;

        var close = ReferenceExtractor.FindMatchingChar(preparedLine, open, '(', ')');
        if (close < 0)
            return;

        var returnStart = SkipWhitespace(preparedLine, close + 1);
        if (!IsGoSignatureReturnStart(preparedLine, returnStart))
            return;

        EmitGoParameterListTypes(preparedLine, open + 1, close, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoSignatureReturnTypes(preparedLine, close + 1, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static bool IsGoStatementKeyword(string value)
        => value is "break" or "case" or "const" or "continue" or "default" or "defer"
            or "else" or "fallthrough" or "for" or "func" or "go" or "goto" or "if"
            or "import" or "package" or "range" or "return" or "select" or "switch"
            or "type" or "var";

    private static bool IsGoSignatureReturnStart(string line, int index)
    {
        if (index >= line.Length)
            return false;

        return line[index] == '(' || IsGoTypeExpressionStart(line, index);
    }

    private static bool IsGoTypeExpressionStart(string line, int index)
    {
        if (index >= line.Length)
            return false;

        return line[index] is '*' or '[' or '~' or '<' || IsIdentifierStart(line[index]);
    }

    private static int FindGoInlineTypeExpressionEnd(string line, int start)
    {
        var squareDepth = 0;
        var parenDepth = 0;
        for (var cursor = start; cursor < line.Length; cursor++)
        {
            switch (line[cursor])
            {
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth == 0)
                        return cursor;
                    parenDepth--;
                    break;
                case ',':
                case '{':
                case '`':
                case '=':
                case ';':
                    if (squareDepth == 0 && parenDepth == 0)
                        return cursor;
                    break;
            }
        }

        return line.Length;
    }

    private static void EmitGoSignatureReturnTypes(
        string preparedLine,
        int start,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var returnStart = SkipWhitespace(preparedLine, start);
        if (returnStart >= preparedLine.Length || preparedLine[returnStart] == '{')
            return;

        if (preparedLine[returnStart] == '(')
        {
            var returnClose = ReferenceExtractor.FindMatchingChar(preparedLine, returnStart, '(', ')');
            if (returnClose > returnStart)
                EmitGoParameterListTypes(preparedLine, returnStart + 1, returnClose, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            return;
        }

        var returnEnd = returnStart;
        while (returnEnd < preparedLine.Length && preparedLine[returnEnd] != '{')
            returnEnd++;

        EmitGoTypeExpression(preparedLine, returnStart, returnEnd, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoTypeParameterConstraints(
        string line,
        int searchStart,
        int searchEnd,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (searchStart < 0 || searchStart >= searchEnd || searchEnd > line.Length)
            return;

        var open = line.IndexOf('[', searchStart, searchEnd - searchStart);
        if (open < 0)
            return;

        var close = ReferenceExtractor.FindMatchingChar(line, open, '[', ']');
        if (close < 0 || close > searchEnd)
            return;

        var list = line.AsSpan(open + 1, close - open - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var fragmentSpan = TrimGoCommaSegment(list.Slice(segmentStart, segmentLength), out var fragmentTrimStart);
            var fragment = fragmentSpan.ToString();
            if (fragment.Length == 0)
                continue;

            var constraintStart = FirstGoTypeParameterConstraintStart(fragment);
            if (constraintStart < 0)
                continue;

            var absoluteStart = open + 1 + segmentStart + Math.Max(0, fragmentTrimStart) + constraintStart;
            EmitGoTypeExpression(fragment[constraintStart..], absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static int FirstGoTypeParameterConstraintStart(string fragment)
    {
        var cursor = 0;
        while (cursor < fragment.Length && char.IsWhiteSpace(fragment[cursor]))
            cursor++;
        if (cursor >= fragment.Length || !IsIdentifierStart(fragment[cursor]))
            return -1;

        cursor++;
        while (cursor < fragment.Length && IsSimpleIdentifierPart(fragment[cursor]))
            cursor++;

        var constraintStart = cursor;
        while (constraintStart < fragment.Length && char.IsWhiteSpace(fragment[constraintStart]))
            constraintStart++;

        return constraintStart < fragment.Length ? constraintStart : -1;
    }

    private static void EmitGoParameterListTypes(
        string line,
        int start,
        int end,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (end <= start)
            return;

        var list = line.AsSpan(start, end - start);
        List<(string Expression, int AbsoluteStart)>? pendingSingleExpressions = null;
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var fragmentSpan = TrimGoCommaSegment(list.Slice(segmentStart, segmentLength), out var fragmentTrimStart);
            var fragment = fragmentSpan.ToString();
            if (fragment.Length == 0)
                continue;

            var absoluteFragmentStart = start + segmentStart + Math.Max(0, fragmentTrimStart);
            var typeStartInFragment = LastWhitespaceSeparatedTokenStart(fragment);
            if (typeStartInFragment < 0)
                continue;
            if (typeStartInFragment == 0)
            {
                (pendingSingleExpressions ??= []).Add((fragment, absoluteFragmentStart));
                continue;
            }

            pendingSingleExpressions?.Clear();
            var expression = fragment[typeStartInFragment..];
            var absoluteStart = absoluteFragmentStart + typeStartInFragment;
            EmitGoTypeExpression(expression, absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }

        if (pendingSingleExpressions is null)
            return;

        foreach (var (expression, absoluteStart) in pendingSingleExpressions)
            EmitGoTypeExpression(expression, absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static ReadOnlySpan<char> TrimGoCommaSegment(ReadOnlySpan<char> segment, out int leading)
    {
        leading = 0;
        while (leading < segment.Length && char.IsWhiteSpace(segment[leading]))
            leading++;

        var length = segment.Length - leading;
        while (length > 0 && char.IsWhiteSpace(segment[leading + length - 1]))
            length--;

        return segment.Slice(leading, length);
    }

    private static void EmitGoTypeExpression(
        string expression,
        int start,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitGoTypeExpressionRange(
            expression,
            0,
            expression.Length,
            start,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitGoTypeExpression(
        string source,
        int start,
        int endExclusive,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitGoTypeExpressionRange(
            source,
            start,
            endExclusive,
            0,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    private static void EmitGoTypeExpressionRange(
        string source,
        int start,
        int endExclusive,
        int absoluteOffset,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        start = Math.Clamp(start, 0, source.Length);
        endExclusive = Math.Clamp(endExclusive, start, source.Length);

        while (start < endExclusive && char.IsWhiteSpace(source[start]))
            start++;

        while (endExclusive > start && char.IsWhiteSpace(source[endExclusive - 1]))
            endExclusive--;

        if (endExclusive <= start)
            return;

        var normalized = start == 0 && endExclusive == source.Length
            ? source
            : source[start..endExclusive];
        var absoluteStart = absoluteOffset + start;
        ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, normalized, absoluteStart, context, lineNumber, resolveContainerForColumn(absoluteStart), "go");
    }
}
