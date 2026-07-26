using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    public static void EmitTypePositionReferences(
        string language,
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        SymbolRecord? container,
        bool isGoImportBlockLine = false)
    {
        switch (language)
        {
            case "c":
            case "cpp":
                EmitCppTypeReferences(language, preparedLine, originalLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
            case "go":
                EmitGoTypeReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, isGoImportBlockLine);
                break;
            case "dart":
                EmitDartTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
            case "vb":
                EmitVbTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
            case "fortran":
                EmitFortranTypeReferences(preparedLine, originalLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, container);
                break;
            case "pascal":
                EmitPascalTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, container);
                break;
            case "objc":
                EmitObjCTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, container);
                break;
            case "haskell":
                EmitHaskellTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
                break;
            case "elixir":
                EmitElixirTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, container);
                break;
            case "lua":
                LuaReferenceExtractor.EmitTypePositionReferences(originalLine, references, seen, fileId, context, lineNumber, container);
                break;
        }
    }

    public static void EmitAdditionalCallReferences(
        string language,
        string preparedLine,
        string originalLine,
        Action<string, int> addCallLikeReference,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames)
    {
        switch (language)
        {
            case "fortran":
                EmitFortranCallReferences(preparedLine, addCallLikeReference);
                break;
            case "pascal":
                EmitPascalCallReferences(preparedLine, addCallLikeReference, definitionNames);
                break;
            case "objc":
                EmitObjCMessageReferences(preparedLine, addCallLikeReference, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
            case "haskell":
                EmitHaskellSpaceCallReferences(preparedLine, addCallLikeReference, definitionNames);
                break;
            case "elixir":
                EmitElixirParenlessCallReferences(preparedLine, addCallLikeReference, definitionNames);
                break;
            case "lua":
                LuaReferenceExtractor.EmitAdditionalCallReferences(preparedLine, addCallLikeReference, references, seen, fileId, context, lineNumber, resolveContainerForColumn, definitionNames);
                break;
            case "smalltalk":
                EmitSmalltalkMessageReferences(preparedLine, addCallLikeReference, definitionNames);
                break;
            case "vb":
                EmitVisualBasicCallByNameReferences(originalLine, preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                EmitVisualBasicEscapedCallReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, definitionNames);
                EmitVisualBasicBareCallReferences(preparedLine, addCallLikeReference, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                EmitVisualBasicBareMemberCallReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
                break;
        }
    }

    private static void EmitCVaArgTypeOperandReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        string language)
    {
        foreach (var functionName in CVaArgFunctionNames)
        {
            var searchStart = 0;
            while (searchStart < line.Length)
            {
                var functionIndex = line.IndexOf(functionName, searchStart, StringComparison.Ordinal);
                if (functionIndex < 0)
                    break;

                searchStart = functionIndex + functionName.Length;
                if (!IsIdentifierAt(line, functionIndex, functionName))
                    continue;

                var open = SkipWhitespace(line, functionIndex + functionName.Length);
                if (open >= line.Length || line[open] != '(')
                    continue;

                var close = ReferenceExtractor.FindMatchingChar(line, open, '(', ')');
                if (close < 0)
                    continue;

                var argumentList = line.Substring(open + 1, close - open - 1);
                var arguments = SplitTopLevelCArgumentSpans(argumentList);
                if (arguments.Count < 2)
                    continue;

                var typeArgument = arguments[1];
                if (typeArgument.Length <= 0)
                    continue;

                var rawType = argumentList.Substring(typeArgument.Start, typeArgument.Length);
                var expression = rawType.Trim();
                if (expression.Length == 0 || !LooksLikeCVaArgTypeOperand(expression))
                    continue;

                var trimStart = rawType.IndexOf(expression, StringComparison.Ordinal);
                var absoluteStart = open + 1 + typeArgument.Start + Math.Max(0, trimStart);
                ReferenceExtractor.AddTypeExpressionSegments(
                    references,
                    seen,
                    fileId,
                    expression,
                    absoluteStart,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart),
                    language);
            }
        }
    }

    private static List<(int Start, int Length)> SplitTopLevelCArgumentSpans(string text)
    {
        var spans = new List<(int Start, int Length)>(4);
        int parenDepth = 0;
        int squareDepth = 0;
        int braceDepth = 0;
        int start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
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
                case ',' when parenDepth == 0 && squareDepth == 0 && braceDepth == 0:
                    spans.Add((start, i - start));
                    start = i + 1;
                    break;
            }
        }

        spans.Add((start, text.Length - start));
        return spans;
    }

    private static bool LooksLikeCVaArgTypeOperand(string expression)
    {
        var cursor = SkipLeadingCTypeQualifiers(expression, 0);
        if (cursor >= expression.Length)
            return false;

        foreach (var keyword in CTypeSpecifierKeywords)
        {
            if (StartsWithKeyword(expression, cursor, keyword))
            {
                cursor = SkipWhitespace(expression, cursor + keyword.Length);
                return cursor < expression.Length && IsIdentifierStart(expression[cursor]);
            }
        }

        if (!IsIdentifierStart(expression[cursor]))
            return false;

        var nameStart = cursor;
        cursor++;
        while (cursor < expression.Length && IsSimpleIdentifierPart(expression[cursor]))
            cursor++;

        return expression.AsSpan(nameStart, cursor - nameStart).EndsWith("_t", StringComparison.Ordinal);
    }

    private static int SkipLeadingCTypeQualifiers(string expression, int cursor)
    {
        while (cursor < expression.Length)
        {
            cursor = SkipWhitespace(expression, cursor);
            var next = cursor;
            if (StartsWithKeyword(expression, cursor, "const"))
                next += "const".Length;
            else if (StartsWithKeyword(expression, cursor, "volatile"))
                next += "volatile".Length;
            else if (StartsWithKeyword(expression, cursor, "restrict"))
                next += "restrict".Length;
            else if (StartsWithKeyword(expression, cursor, "_Atomic"))
                next += "_Atomic".Length;
            else
                return cursor;

            cursor = next;
        }

        return cursor;
    }

    private static bool StartsWithKeyword(string line, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > line.Length)
            return false;
        if (!line.AsSpan(index, keyword.Length).SequenceEqual(keyword))
            return false;

        var beforeOk = index == 0 || !IsSimpleIdentifierPart(line[index - 1]);
        var after = index + keyword.Length;
        var afterOk = after >= line.Length || !IsSimpleIdentifierPart(line[after]);
        return beforeOk && afterOk;
    }

    private static void EmitDartTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        TypedLanguageReferenceExtractor.EmitKeywordFollowingTypeReferences(
            preparedLine,
            ["extends", "with", "implements", "on", "as", "is"],
            "dart",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(preparedLine, 0, preparedLine.Length, "dart", references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        TypedLanguageReferenceExtractor.EmitColonVariableTypeReferences(preparedLine, ["final", "var", "late", "const"], "dart", references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        var hasDartDeclarationTerminator = preparedLine.IndexOf('=') >= 0
            || preparedLine.IndexOf(';') >= 0;
        var hasDartUppercaseTypeMarker = ContainsAsciiUppercase(preparedLine);
        if (hasDartDeclarationTerminator && hasDartUppercaseTypeMarker)
        {
            foreach (Match match in Regex.EnumerateMatches(DartVariableTypeRegex, preparedLine))
            {
                var group = match.Groups["type"];
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, group.Value, group.Index, context, lineNumber, resolveContainerForColumn(group.Index), "dart");
            }
        }

        var hasDartParen = preparedLine.IndexOf('(') >= 0;
        var signatureMatch = hasDartParen && hasDartUppercaseTypeMarker
            ? DartFunctionSignatureRegex.Match(preparedLine)
            : Match.Empty;
        if (signatureMatch.Success)
        {
            var returnGroup = signatureMatch.Groups["return"];
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, returnGroup.Value, returnGroup.Index, context, lineNumber, resolveContainerForColumn(returnGroup.Index), "dart");

            var parametersGroup = signatureMatch.Groups["params"];
            foreach (Match parameterMatch in Regex.EnumerateMatches(DartParameterTypeRegex, parametersGroup.Value))
            {
                var typeGroup = parameterMatch.Groups["type"];
                var absoluteIndex = parametersGroup.Index + typeGroup.Index;
                ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, typeGroup.Value, absoluteIndex, context, lineNumber, resolveContainerForColumn(absoluteIndex), "dart");
            }
        }

        var hasDartCtorMarker = hasDartParen
            && hasDartUppercaseTypeMarker
            && (preparedLine.IndexOf("new", StringComparison.Ordinal) >= 0
                || preparedLine.IndexOf("const", StringComparison.Ordinal) >= 0);
        if (hasDartCtorMarker)
        {
            foreach (Match match in Regex.EnumerateMatches(DartCtorRegex, preparedLine))
            {
                var group = match.Groups["name"];
                ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "instantiate", context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }
    }

    private static int FirstNonWhitespaceIndex(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsWhiteSpace(value[i]))
                return i;
        }

        return -1;
    }

    private static bool StartsWithKeywordIgnoringLeadingWhitespace(string value, string keyword)
    {
        var start = FirstNonWhitespaceIndex(value);
        if (start < 0 || value.Length - start < keyword.Length)
            return false;

        if (!value.AsSpan(start, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var boundary = start + keyword.Length;
        return boundary >= value.Length || !IsSimpleIdentifierPart(value[boundary]);
    }

    private static bool StartsWithOrdinalKeywordIgnoringLeadingWhitespace(string value, string keyword)
    {
        var start = FirstNonWhitespaceIndex(value);
        if (start < 0 || value.Length - start < keyword.Length)
            return false;

        if (!value.AsSpan(start, keyword.Length).Equals(keyword, StringComparison.Ordinal))
            return false;

        var boundary = start + keyword.Length;
        return boundary >= value.Length || !IsSimpleIdentifierPart(value[boundary]);
    }

    private static bool StartsWithCharIgnoringLeadingWhitespace(string value, char marker)
    {
        var start = FirstNonWhitespaceIndex(value);
        return start >= 0 && value[start] == marker;
    }

    private static bool ContainsKeywordIgnoringCase(string value, string keyword)
    {
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var matchIndex = value.IndexOf(keyword, searchStart, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
                return false;

            var beforeBoundary = matchIndex == 0 || !IsSimpleIdentifierPart(value[matchIndex - 1]);
            var afterIndex = matchIndex + keyword.Length;
            if (beforeBoundary && (afterIndex >= value.Length || !IsSimpleIdentifierPart(value[afterIndex])))
                return true;

            searchStart = matchIndex + 1;
        }

        return false;
    }

    private static bool ContainsOrdinalKeyword(string value, string keyword)
    {
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var matchIndex = value.IndexOf(keyword, searchStart, StringComparison.Ordinal);
            if (matchIndex < 0)
                return false;

            var beforeBoundary = matchIndex == 0 || !IsSimpleIdentifierPart(value[matchIndex - 1]);
            var afterIndex = matchIndex + keyword.Length;
            if (beforeBoundary && (afterIndex >= value.Length || !IsSimpleIdentifierPart(value[afterIndex])))
                return true;

            searchStart = matchIndex + 1;
        }

        return false;
    }

    private static bool CanStartVisualBasicIdentifierPattern(char value) =>
        value == '['
        || CanStartAsciiIdentifierPattern(value);

    private static bool CanStartFortranIdentifierPattern(char value) =>
        CanStartAsciiIdentifierPattern(value);

    private static bool CanStartAsciiIdentifierPattern(char value) =>
        value == '_'
        || value is >= 'A' and <= 'Z'
        || value is >= 'a' and <= 'z';

    private static bool ShouldSkipVisualBasicBareCall(string rawName, string tail)
    {
        if (tail.StartsWith('(') || tail.StartsWith('=') || tail.StartsWith(':'))
            return true;
        if (tail.StartsWith("As ", StringComparison.OrdinalIgnoreCase))
            return true;

        var firstSegment = rawName;
        var dotIndex = rawName.IndexOf('.');
        if (dotIndex >= 0)
            firstSegment = rawName[..dotIndex];
        firstSegment = NormalizeVbIdentifierSegment(firstSegment);

        return IsVisualBasicBareCallStatementHead(firstSegment);
    }

    private static bool IsVisualBasicBareCallStatementHead(string name) =>
        name.Equals("Public", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Private", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Protected", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Friend", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Shared", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Overrides", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Overridable", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NotOverridable", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MustOverride", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Overloads", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Shadows", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Async", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Iterator", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Partial", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Declare", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Dim", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ReDim", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Const", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Let", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Loop", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ElseIf", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Finally", StringComparison.OrdinalIgnoreCase);

    private static void EmitVisualBasicBareMemberCallReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var firstNonWhitespace = FirstNonWhitespaceIndex(preparedLine);
        if (firstNonWhitespace < 0 || preparedLine[firstNonWhitespace] != '.')
            return;

        var match = VbBareMemberCallRegex.Match(preparedLine);
        if (!match.Success)
            return;

        var group = match.Groups["name"];
        var tail = match.Groups["tail"].Value.TrimStart();
        if (tail.StartsWith('(') || tail.StartsWith('=') || tail.StartsWith(':') || tail.StartsWith("As ", StringComparison.OrdinalIgnoreCase))
            return;

        var rawName = group.Value;
        var name = NormalizeVbIdentifierSegment(rawName);
        var nameIndex = rawName.StartsWith('[') ? group.Index + 1 : group.Index;
        ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "call", context, lineNumber, resolveContainerForColumn(nameIndex));
    }

    private static bool IsVisualBasicMemberImplementsClause(string line, int implementsIndex)
    {
        var head = line[..implementsIndex];
        return head.Contains(')')
            || head.Contains(" Property ", StringComparison.OrdinalIgnoreCase)
            || head.TrimStart().StartsWith("Property ", StringComparison.OrdinalIgnoreCase)
            || head.Contains(" Event ", StringComparison.OrdinalIgnoreCase)
            || head.TrimStart().StartsWith("Event ", StringComparison.OrdinalIgnoreCase);
    }

    private static void EmitVisualBasicImplementsOwnerReferences(
        string list,
        int listStart,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var segment = list.Substring(segmentStart, segmentLength);
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
                continue;

            var dotIndex = LastVisualBasicQualifierDot(trimmed);
            if (dotIndex <= 0)
                continue;

            var owner = trimmed[..dotIndex].Trim();
            if (owner.Length == 0)
                continue;

            var ownerOffset = segment.IndexOf(owner, StringComparison.Ordinal);
            var ownerStart = listStart + segmentStart + Math.Max(0, ownerOffset);
            ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, owner, ownerStart, context, lineNumber, resolveContainerForColumn(ownerStart), "vb");
        }
    }

    private static int LastVisualBasicQualifierDot(string value)
    {
        var inEscapedIdentifier = false;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (value[i] == ']')
            {
                inEscapedIdentifier = true;
                continue;
            }

            if (value[i] == '[')
            {
                inEscapedIdentifier = false;
                continue;
            }

            if (value[i] == '.' && !inEscapedIdentifier)
                return i;
        }

        return -1;
    }

    private static bool ShouldSkipVisualBasicEscapedCall(
        string line,
        int nameIndex,
        string name,
        IReadOnlySet<string>? definitionNames)
    {
        var previous = GetPreviousSimpleWord(line, nameIndex);
        if (previous.Length == 0)
            return false;

        if (string.Equals(previous, "New", StringComparison.OrdinalIgnoreCase)
            || string.Equals(previous, "RaiseEvent", StringComparison.OrdinalIgnoreCase))
            return true;

        if (definitionNames?.Contains(name) != true)
            return false;

        return previous.Equals("Sub", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Function", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Property", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Event", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Delegate", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Class", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Structure", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Interface", StringComparison.OrdinalIgnoreCase)
            || previous.Equals("Enum", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPreviousSimpleWord(string line, int index)
    {
        var cursor = index - 1;
        while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
            cursor--;
        if (cursor < 0)
            return string.Empty;

        var end = cursor + 1;
        while (cursor >= 0 && IsSimpleIdentifierPart(line[cursor]))
            cursor--;

        return line[(cursor + 1)..end];
    }

}
