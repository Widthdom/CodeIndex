using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static void EmitGoBuiltinTypeArgumentReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in GoBuiltinTypeArgumentRegex.Matches(line))
        {
            var open = line.IndexOf('(', match.Index);
            if (open < 0)
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '(', ')');
            if (close < 0)
                continue;

            var argumentList = line[(open + 1)..close];
            var firstArgument = ReferenceExtractor.GetFirstTopLevelCommaSpan(argumentList);
            if (firstArgument.Length <= 0)
                continue;

            var rawType = argumentList.Substring(firstArgument.Start, firstArgument.Length);
            var expression = rawType.Trim();
            if (expression.Length == 0)
                continue;

            var trimStart = rawType.IndexOf(expression, StringComparison.Ordinal);
            var absoluteStart = open + 1 + firstArgument.Start + Math.Max(0, trimStart);
            EmitGoTypeExpression(expression, absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoTypeAssertionReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in GoTypeAssertionRegex.Matches(line))
        {
            var group = match.Groups["type"];
            var expression = group.Value.Trim();
            if (expression.Length == 0 || string.Equals(expression, "type", StringComparison.Ordinal))
                continue;

            var trimStart = group.Value.IndexOf(expression, StringComparison.Ordinal);
            EmitGoTypeExpression(expression, group.Index + Math.Max(0, trimStart), references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoTypeSwitchCaseReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var match = GoTypeSwitchCaseRegex.Match(line);
        if (!match.Success)
            return;

        var group = match.Groups["types"];
        var typeList = group.ValueSpan;
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeList))
        {
            var expressionSpan = TrimGoCommaSegment(typeList.Slice(segmentStart, segmentLength), out var trimStart);
            var expression = expressionSpan.ToString();
            if (expression.Length == 0
                || expression is "nil" or "default"
                || !IsLikelyGoTypeSwitchCaseType(expression))
            {
                continue;
            }

            EmitGoTypeExpression(expression, group.Index + segmentStart + Math.Max(0, trimStart), references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static bool IsLikelyGoTypeSwitchCaseType(string expression)
    {
        var cursor = 0;
        var hasPointerPrefix = false;
        while (cursor < expression.Length)
        {
            cursor = SkipWhitespace(expression, cursor);
            if (cursor >= expression.Length || expression[cursor] != '*')
                break;

            hasPointerPrefix = true;
            cursor++;
        }

        if (cursor >= expression.Length)
            return false;
        if (expression[cursor] == '[')
            return true;
        if (hasPointerPrefix && char.IsUpper(expression[cursor]))
            return true;
        if (hasPointerPrefix && expression.IndexOf('.', cursor) >= 0)
            return true;

        return StartsWithKeyword(expression, cursor, "map")
            || StartsWithKeyword(expression, cursor, "chan")
            || StartsWithKeyword(expression, cursor, "func")
            || StartsWithKeyword(expression, cursor, "interface")
            || StartsWithKeyword(expression, cursor, "struct");
    }

    private static void EmitGoChannelElementTypeReferences(
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
            var chanIndex = line.IndexOf("chan", searchStart, StringComparison.Ordinal);
            if (chanIndex < 0)
                return;

            searchStart = chanIndex + "chan".Length;
            if (!IsIdentifierAt(line, chanIndex, "chan"))
                continue;

            var elementStart = SkipWhitespace(line, searchStart);
            if (elementStart + 1 < line.Length && line[elementStart] == '<' && line[elementStart + 1] == '-')
                elementStart = SkipWhitespace(line, elementStart + 2);

            if (elementStart >= line.Length || !IsGoTypeExpressionStart(line, elementStart))
                continue;

            var elementEnd = FindGoInlineTypeExpressionEnd(line, elementStart);
            if (elementEnd <= elementStart)
                continue;

            EmitGoTypeExpression(line[elementStart..elementEnd], elementStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoFunctionLiteralSignatureTypes(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in GoFunctionLiteralRegex.Matches(line))
        {
            var open = line.IndexOf('(', match.Index);
            if (open < 0)
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '(', ')');
            if (close < 0)
                continue;

            EmitGoParameterListTypes(line, open + 1, close, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            EmitGoSignatureReturnTypes(line, close + 1, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoGenericCallTypeArgumentReferences(
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
            if (!HasGoIdentifierBeforeBracket(line, open))
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '[', ']');
            if (close < 0)
                continue;

            var afterClose = SkipWhitespace(line, close + 1);
            if (afterClose >= line.Length || line[afterClose] != '(')
                continue;

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
    }

    private static void EmitGoFunctionTypeSignatureTypes(
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
            var funcIndex = line.IndexOf("func", searchStart, StringComparison.Ordinal);
            if (funcIndex < 0)
                return;

            searchStart = funcIndex + "func".Length;
            if (!IsIdentifierAt(line, funcIndex, "func"))
                continue;

            var open = SkipWhitespace(line, searchStart);
            if (open >= line.Length || line[open] != '(')
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '(', ')');
            if (close < 0)
                continue;

            EmitGoParameterListTypes(line, open + 1, close, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            EmitGoSignatureReturnTypes(line, close + 1, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoGenericCompositeLiteralReferences(
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

            var typeName = line.Substring(nameStart, nameLength);
            if (typeName.Length == 0 || !char.IsUpper(typeName[0]))
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '[', ']');
            if (close < 0)
                continue;

            var afterClose = SkipWhitespace(line, close + 1);
            if (afterClose >= line.Length || line[afterClose] != '{')
                continue;

            if (!IsGoCompositeLiteralContext(line, nameStart, nameLength))
                continue;

            ReferenceExtractor.AddReference(references, seen, fileId, typeName, nameStart, "instantiate", context, lineNumber, resolveContainerForColumn(nameStart));

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
    }

    private static void EmitGoInlineStructFieldTypeReferences(
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
            var structIndex = line.IndexOf("struct", searchStart, StringComparison.Ordinal);
            if (structIndex < 0)
                return;

            searchStart = structIndex + "struct".Length;
            if (!IsIdentifierAt(line, structIndex, "struct"))
                continue;

            var open = SkipWhitespace(line, searchStart);
            if (open >= line.Length || line[open] != '{')
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '{', '}');
            if (close <= open + 1)
                continue;

            var body = line[(open + 1)..close];
            var bodyStart = open + 1;
            foreach (var (fieldStart, fieldLength) in SplitGoInlineStructFieldSpans(body))
                EmitGoInlineStructFieldType(body.Substring(fieldStart, fieldLength), bodyStart + fieldStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static List<(int Start, int Length)> SplitGoInlineStructFieldSpans(string body)
    {
        var spans = new List<(int Start, int Length)>(4);
        var fieldStart = 0;
        var squareDepth = 0;
        var parenDepth = 0;
        for (var cursor = 0; cursor < body.Length; cursor++)
        {
            switch (body[cursor])
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
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case ';':
                    if (squareDepth == 0 && parenDepth == 0)
                    {
                        spans.Add((fieldStart, cursor - fieldStart));
                        fieldStart = cursor + 1;
                    }
                    break;
            }
        }

        spans.Add((fieldStart, body.Length - fieldStart));
        return spans;
    }

    private static void EmitGoInlineStructFieldType(
        string rawField,
        int rawFieldStart,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var tagStart = rawField.IndexOf('`');
        if (tagStart >= 0)
            rawField = rawField[..tagStart];

        var field = rawField.Trim();
        if (field.Length == 0)
            return;

        var fieldTrimStart = rawField.IndexOf(field, StringComparison.Ordinal);
        var absoluteFieldStart = rawFieldStart + Math.Max(0, fieldTrimStart);
        var typeStart = LastWhitespaceSeparatedTokenStart(field);
        if (typeStart < 0)
            return;

        var expression = typeStart == 0 ? field : field[typeStart..];
        EmitGoTypeExpression(expression, absoluteFieldStart + typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoInlineInterfaceMemberTypeReferences(
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
            var interfaceIndex = line.IndexOf("interface", searchStart, StringComparison.Ordinal);
            if (interfaceIndex < 0)
                return;

            searchStart = interfaceIndex + "interface".Length;
            if (!IsIdentifierAt(line, interfaceIndex, "interface"))
                continue;

            var open = SkipWhitespace(line, searchStart);
            if (open >= line.Length || line[open] != '{')
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '{', '}');
            if (close <= open + 1)
                continue;

            var body = line[(open + 1)..close];
            var bodyStart = open + 1;
            foreach (var (memberStart, memberLength) in SplitGoInlineStructFieldSpans(body))
                EmitGoInlineInterfaceMemberTypes(line, bodyStart + memberStart, bodyStart + memberStart + memberLength, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoInlineInterfaceMemberTypes(
        string line,
        int memberStart,
        int memberEnd,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var cursor = SkipWhitespace(line, memberStart);
        if (cursor >= memberEnd)
            return;

        if (!IsIdentifierStart(line[cursor]))
        {
            EmitGoInlineInterfaceEmbeddedType(line, cursor, memberEnd, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            return;
        }

        var nameStart = cursor;
        cursor++;
        while (cursor < memberEnd && IsSimpleIdentifierPart(line[cursor]))
            cursor++;

        var name = line[nameStart..cursor];
        if (IsGoStatementKeyword(name))
            return;

        var open = SkipWhitespace(line, cursor);
        if (open < memberEnd && line[open] == '(')
        {
            var close = ReferenceExtractor.FindMatchingChar(line, open, '(', ')');
            if (close > open && close <= memberEnd)
            {
                EmitGoParameterListTypes(line, open + 1, close, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                EmitGoSignatureReturnTypesInRange(line, close + 1, memberEnd, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            }

            return;
        }

        EmitGoInlineInterfaceEmbeddedType(line, nameStart, memberEnd, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoInlineInterfaceEmbeddedType(
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
        var typeStart = SkipWhitespace(line, start);
        if (typeStart >= end)
            return;

        var expression = line[typeStart..end].Trim();
        if (expression.Length == 0)
            return;

        EmitGoTypeExpression(expression, typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoSignatureReturnTypesInRange(
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
        var returnStart = SkipWhitespace(line, start);
        if (returnStart >= end || line[returnStart] == '{')
            return;

        if (line[returnStart] == '(')
        {
            var returnClose = ReferenceExtractor.FindMatchingChar(line, returnStart, '(', ')');
            if (returnClose > returnStart && returnClose <= end)
                EmitGoParameterListTypes(line, returnStart + 1, returnClose, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            return;
        }

        EmitGoTypeExpression(line, returnStart, end, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoMapCompositeLiteralTypeReferences(
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
            var mapIndex = line.IndexOf("map", searchStart, StringComparison.Ordinal);
            if (mapIndex < 0)
                return;

            searchStart = mapIndex + "map".Length;
            if (!IsIdentifierAt(line, mapIndex, "map"))
                continue;

            var open = SkipWhitespace(line, searchStart);
            if (open >= line.Length || line[open] != '[')
                continue;

            var close = ReferenceExtractor.FindMatchingChar(line, open, '[', ']');
            if (close < 0)
                continue;

            var valueStart = SkipWhitespace(line, close + 1);
            if (valueStart >= line.Length || !IsGoTypeExpressionStart(line, valueStart))
                continue;

            var valueEnd = FindGoInlineTypeExpressionEnd(line, valueStart);
            var literalOpen = SkipWhitespace(line, valueEnd);
            if (literalOpen >= line.Length || line[literalOpen] != '{')
                continue;

            var keyExpression = line[(open + 1)..close].Trim();
            if (keyExpression.Length > 0)
            {
                var keyStart = line.IndexOf(keyExpression, open + 1, StringComparison.Ordinal);
                EmitGoTypeExpression(keyExpression, keyStart >= 0 ? keyStart : open + 1, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            }

            EmitGoTypeExpression(line[valueStart..valueEnd], valueStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoArraySliceCompositeLiteralTypeReferences(
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
            var close = ReferenceExtractor.FindMatchingChar(line, open, '[', ']');
            if (close < 0)
                continue;

            var elementStart = SkipWhitespace(line, close + 1);
            if (elementStart >= line.Length || !IsGoTypeExpressionStart(line, elementStart))
                continue;

            var elementEnd = FindGoInlineTypeExpressionEnd(line, elementStart);
            var literalOpen = SkipWhitespace(line, elementEnd);
            if (literalOpen >= line.Length || line[literalOpen] != '{')
                continue;

            if (!IsGoCompositeLiteralContext(line, open, elementEnd - open))
                continue;

            EmitGoTypeExpression(line[elementStart..elementEnd], elementStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoParenthesizedTypeConversionReferences(
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

            var afterClose = SkipWhitespace(line, close + 1);
            if (afterClose >= line.Length || line[afterClose] != '(')
                continue;

            var rawExpression = line[(open + 1)..close];
            var expression = rawExpression.Trim();
            if (!IsLikelyGoParenthesizedConversionType(expression))
                continue;

            var trimStart = rawExpression.IndexOf(expression, StringComparison.Ordinal);
            EmitGoTypeExpression(expression, open + 1 + Math.Max(0, trimStart), references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static void EmitGoCompositeTypeConversionReferences(
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
            var typeStart = NextGoCompositeConversionTypeStart(line, searchStart);
            if (typeStart < 0)
                return;

            searchStart = typeStart + 1;
            if (!IsGoTypeExpressionValueContext(line, typeStart))
                continue;

            var typeEnd = FindGoConversionTypeExpressionEnd(line, typeStart);
            if (typeEnd <= typeStart)
                continue;

            var open = SkipWhitespace(line, typeEnd);
            if (open >= line.Length || line[open] != '(')
                continue;
            if (ReferenceExtractor.FindMatchingChar(line, open, '(', ')') < 0)
                continue;

            EmitGoTypeExpression(line[typeStart..typeEnd], typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

}
