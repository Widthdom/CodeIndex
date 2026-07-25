using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static int NextGoCompositeConversionTypeStart(string line, int searchStart)
    {
        for (var cursor = searchStart; cursor < line.Length; cursor++)
        {
            if (line[cursor] == '[')
                return cursor;
            if (IsIdentifierAt(line, cursor, "map") || IsIdentifierAt(line, cursor, "chan"))
                return cursor;
        }

        return -1;
    }

    private static int FindGoConversionTypeExpressionEnd(string line, int start)
    {
        var squareDepth = 0;
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
                case ',':
                case '{':
                case '`':
                case '=':
                case ';':
                    if (squareDepth == 0)
                        return cursor;
                    break;
            }
        }

        return line.Length;
    }

    private static bool IsGoTypeExpressionValueContext(string line, int start)
    {
        var previous = start - 1;
        while (previous >= 0 && char.IsWhiteSpace(line[previous]))
            previous--;
        if (previous < 0)
            return true;
        if (line[previous] is '=' or ':' or '(' or '[' or '{' or ',' or '!' or '&' or '*')
            return true;

        var tokenEnd = previous + 1;
        while (previous >= 0 && IsSimpleIdentifierPart(line[previous]))
            previous--;
        var token = line[(previous + 1)..tokenEnd];
        return string.Equals(token, "return", StringComparison.Ordinal);
    }

    private static bool IsLikelyGoParenthesizedConversionType(string expression)
    {
        if (expression.Length == 0 || expression.Contains(','))
            return false;

        var cursor = 0;
        while (cursor < expression.Length && char.IsWhiteSpace(expression[cursor]))
            cursor++;
        var isPointerConversion = cursor < expression.Length && expression[cursor] == '*';
        while (cursor < expression.Length && expression[cursor] == '*')
            cursor = SkipWhitespace(expression, cursor + 1);

        if (cursor >= expression.Length || !IsIdentifierStart(expression[cursor]))
        {
            return cursor < expression.Length && expression[cursor] == '[';
        }

        if (StartsWithKeyword(expression, cursor, "map")
            || StartsWithKeyword(expression, cursor, "chan"))
        {
            return true;
        }

        var lastSegmentStart = cursor;
        while (cursor < expression.Length)
        {
            if (IsSimpleIdentifierPart(expression[cursor]))
            {
                cursor++;
                continue;
            }

            if (expression[cursor] != '.')
                return false;

            cursor++;
            if (cursor >= expression.Length || !IsIdentifierStart(expression[cursor]))
                return false;
            lastSegmentStart = cursor;
            cursor++;
        }

        return isPointerConversion || expression.Contains('.');
    }

    private static void EmitGoMethodExpressionReceiverTypeReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitGoParenthesizedMethodExpressionReceiverTypes(line, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoBareMethodExpressionReceiverTypes(line, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoGenericMethodExpressionReceiverTypes(line, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoParenthesizedMethodExpressionReceiverTypes(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var searchStart = 0;
        while (searchStart < line.Length)
        {
            var open = line.IndexOf('(', searchStart);
            if (open < 0)
                return;

            searchStart = open + 1;
            var close = ReferenceExtractor.FindMatchingChar(line, open, '(', ')');
            if (close <= open + 1)
                continue;

            var dot = SkipWhitespace(line, close + 1);
            if (dot >= line.Length || line[dot] != '.')
                continue;

            var methodStart = dot + 1;
            if (methodStart >= line.Length || !IsIdentifierStart(line[methodStart]))
                continue;

            var rawExpression = line[(open + 1)..close];
            var expression = rawExpression.Trim();
            if (!IsLikelyGoMethodExpressionReceiverType(expression))
                continue;

            var trimStart = rawExpression.IndexOf(expression, StringComparison.Ordinal);
            EmitGoTypeExpression(expression, open + 1 + Math.Max(0, trimStart), references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoBareMethodExpressionReceiverTypes(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var dot = 1; dot < line.Length - 1; dot++)
        {
            if (line[dot] != '.')
                continue;
            if (!IsSimpleIdentifierPart(line[dot - 1]) || !IsIdentifierStart(line[dot + 1]))
                continue;

            var receiverStart = dot - 1;
            while (receiverStart >= 0 && IsSimpleIdentifierPart(line[receiverStart]))
                receiverStart--;
            receiverStart++;

            var receiverName = line[receiverStart..dot];
            if (receiverName.Length == 0 || !char.IsUpper(receiverName[0]))
                continue;

            ReferenceExtractor.AddReference(references, seen, fileId, receiverName, receiverStart, "type_reference", context, lineNumber, resolveContainerForColumn(receiverStart));
        }
    }

    private static bool IsLikelyGoMethodExpressionReceiverType(string expression)
    {
        if (expression.Length == 0 || expression.Contains(','))
            return false;

        var cursor = 0;
        while (cursor < expression.Length && char.IsWhiteSpace(expression[cursor]))
            cursor++;
        if (cursor < expression.Length && expression[cursor] == '*')
            cursor = SkipWhitespace(expression, cursor + 1);

        if (cursor >= expression.Length || !IsIdentifierStart(expression[cursor]))
            return false;

        if (IsLikelyGoGenericReceiverTypeExpression(expression, cursor))
            return true;

        var lastSegmentStart = cursor;
        while (cursor < expression.Length)
        {
            if (IsSimpleIdentifierPart(expression[cursor]))
            {
                cursor++;
                continue;
            }

            if (expression[cursor] != '.')
                return false;

            cursor++;
            if (cursor >= expression.Length || !IsIdentifierStart(expression[cursor]))
                return false;
            lastSegmentStart = cursor;
            cursor++;
        }

        return expression.Contains('.') || char.IsUpper(expression[lastSegmentStart]);
    }

    private static void EmitGoGenericMethodExpressionReceiverTypes(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var searchStart = 0;
        while (searchStart < line.Length)
        {
            var open = line.IndexOf('[', searchStart);
            if (open < 0)
                return;

            searchStart = open + 1;
            if (!TryGetGoIdentifierBeforeBracket(line, open, out var nameStart, out var nameLength))
                continue;
            if (nameLength == 0 || !char.IsUpper(line[nameStart]))
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '[', ']');
            if (close < 0)
                continue;

            var dot = SkipWhitespace(line, close + 1);
            if (dot >= line.Length || line[dot] != '.')
                continue;

            var methodStart = dot + 1;
            if (methodStart >= line.Length || !IsIdentifierStart(line[methodStart]))
                continue;

            EmitGoTypeExpression(line[nameStart..(close + 1)], nameStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static bool IsLikelyGoGenericReceiverTypeExpression(string expression, int receiverStart)
    {
        var open = expression.IndexOf('[', receiverStart);
        if (open < 0 || !ContainsLikelyGoTypeArgument(expression[open..]))
            return false;

        var firstSegmentStart = receiverStart;
        var firstSegmentEnd = firstSegmentStart;
        while (firstSegmentEnd < expression.Length && IsSimpleIdentifierPart(expression[firstSegmentEnd]))
            firstSegmentEnd++;

        if (firstSegmentEnd <= firstSegmentStart)
            return false;
        if (char.IsUpper(expression[firstSegmentStart]))
            return true;

        var afterFirst = SkipWhitespace(expression, firstSegmentEnd);
        return afterFirst < expression.Length && expression[afterFirst] == '.';
    }

    private static void EmitGoGenericInstantiationTypeArgumentReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (line.IndexOf("func", StringComparison.Ordinal) >= 0
            && GoFuncRegex.IsMatch(line))
        {
            return;
        }

        var searchStart = 0;
        while (searchStart < line.Length)
        {
            var open = line.IndexOf('[', searchStart);
            if (open < 0)
                return;

            searchStart = open + 1;
            if (!TryGetGoIdentifierBeforeBracket(line, open, out var nameStart, out var nameLength))
                continue;
            if (nameLength == 0 || !char.IsUpper(line[nameStart]))
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '[', ']');
            if (close < 0)
                continue;

            var afterClose = SkipWhitespace(line, close + 1);
            if (afterClose < line.Length && line[afterClose] is '(' or '{')
                continue;
            if (afterClose < line.Length && !IsGoGenericInstantiationTerminator(line[afterClose]))
                continue;

            EmitGoGenericTypeArgumentList(line, open, close, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static bool IsGoGenericInstantiationTerminator(char ch)
        => char.IsWhiteSpace(ch) || ch is ',' or ')' or ']' or '}' or ';';

    private static void EmitGoGenericTypeArgumentList(
        string line,
        int open,
        int close,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var typeArguments = line.AsSpan(open + 1, close - open - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeArguments))
        {
            var expressionSpan = TrimGoCommaSegment(typeArguments.Slice(segmentStart, segmentLength), out var trimStart);
            var expression = expressionSpan.ToString();
            if (expression.Length == 0 || !ContainsLikelyGoTypeArgument(expression))
                continue;

            var absoluteStart = open + 1 + segmentStart + Math.Max(0, trimStart);
            EmitGoTypeExpression(expression, absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static bool HasGoIdentifierBeforeBracket(string line, int openBracket)
        => TryGetGoIdentifierBeforeBracket(line, openBracket, out _, out _);

    private static bool TryGetGoIdentifierBeforeBracket(string line, int openBracket, out int start, out int length)
    {
        start = -1;
        length = 0;
        var cursor = openBracket - 1;
        while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
            cursor--;
        if (cursor < 0 || !IsSimpleIdentifierPart(line[cursor]))
            return false;

        var end = cursor + 1;
        while (cursor >= 0 && IsSimpleIdentifierPart(line[cursor]))
            cursor--;

        start = cursor + 1;
        length = end - start;
        return true;
    }

    private static bool ContainsLikelyGoTypeArgument(string expression)
    {
        for (var i = 0; i < expression.Length; i++)
        {
            if (!IsIdentifierStart(expression[i]))
                continue;

            var start = i;
            i++;
            while (i < expression.Length && IsSimpleIdentifierPart(expression[i]))
                i++;

            if (char.IsUpper(expression[start]))
                return true;
        }

        return false;
    }

    private static bool ContainsGoUppercaseAscii(string line)
    {
        foreach (var ch in line)
        {
            if (ch is >= 'A' and <= 'Z')
                return true;
        }

        return false;
    }

    private static bool IsGoTypeDeclarationBodyStart(string line, int index)
    {
        if (StartsWithKeyword(line, index, "struct")
            || StartsWithKeyword(line, index, "interface")
            || StartsWithKeyword(line, index, "func")
            || StartsWithKeyword(line, index, "map")
            || StartsWithKeyword(line, index, "chan"))
        {
            return true;
        }

        return line[index] is '*' or '[' or '~' || IsIdentifierStart(line[index]);
    }

    private static bool IsGoCompositeLiteralContext(string line, int nameIndex, int nameLength)
    {
        var openBraceIndex = line.IndexOf('{', nameIndex + nameLength);
        if (openBraceIndex < 0)
            return false;

        var trimmed = line.TrimStart();
        var firstBraceIndex = line.IndexOf('{');
        if (trimmed.StartsWith("func ", StringComparison.Ordinal) && firstBraceIndex == openBraceIndex)
            return false;

        var previous = nameIndex - 1;
        while (previous >= 0 && char.IsWhiteSpace(line[previous]))
            previous--;
        if (previous < 0)
            return false;

        if (line[previous] is '=' or ':' or '(' or '[' or '{' or ',' or '!' or '&' or '*')
            return true;
        if (line[previous] == '.')
            return previous > 0 && IsSimpleIdentifierPart(line[previous - 1]);
        if (line[previous] == ']')
            return !trimmed.StartsWith("func ", StringComparison.Ordinal);

        var tokenEnd = previous + 1;
        while (previous >= 0 && IsSimpleIdentifierPart(line[previous]))
            previous--;
        var token = line[(previous + 1)..tokenEnd];
        return string.Equals(token, "return", StringComparison.Ordinal);
    }

}
