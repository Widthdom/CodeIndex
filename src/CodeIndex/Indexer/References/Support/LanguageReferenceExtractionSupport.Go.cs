using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    private static readonly Regex GoImportRegex = new(
        @"^\s*import\s+(?:\(\s*)?(?:(?<alias>[A-Za-z_]\w*|\.)\s+)?""(?<name>[^""]+)""(?:\s*\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoImportBlockStartRegex = new(
        @"^\s*import\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoImportBlockEntryRegex = new(
        @"^\s*(?:(?<alias>[A-Za-z_]\w*|\.)\s+)?""(?<name>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoVarTypeRegex = new(
        @"\b(?:var|const)\s+[A-Za-z_]\w*\s+(?<type>[\*\[\]\w.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoFieldTypeRegex = new(
        @"^\s*(?!(?:package|import|func|type|var|const|return|defer|go|break|continue|goto|if|for|switch|select|case|default|else)\b)[A-Za-z_]\w*\s+(?<type>[\*\[\]\w.]+)(?:\s|`|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoTypeAliasRegex = new(
        @"^\s*type\s+[A-Za-z_]\w*(?:\[[^\]]+\])?\s+=?\s*(?<type>[\*\[\]\w.]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex[] GoTypeReferenceRegexes = [GoVarTypeRegex, GoFieldTypeRegex, GoTypeAliasRegex];
    private static readonly Regex GoFuncRegex = new(
        @"^\s*func\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoCompositeLiteralRegex = new(
        @"(?<!\btype\s)(?<name>[A-Z]\w*)\s*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoBuiltinTypeArgumentRegex = new(
        @"\b(?:make|new)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoTypeAssertionRegex = new(
        @"\.\s*\(\s*(?<type>[^()\r\n]+?)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoTypeSwitchCaseRegex = new(
        @"^\s*case\s+(?<types>.+?)\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoFunctionLiteralRegex = new(
        @"\bfunc\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GoBranchLabelRegex = new(
        @"\b(?:goto|break|continue)\s+(?<name>[A-Za-z_]\w*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool[]? BuildGoImportBlockLineMap(IReadOnlyList<string> originalLines)
    {
        bool[]? result = null;
        var inImportBlock = false;
        var inBlockComment = false;

        for (var i = 0; i < originalLines.Count; i++)
        {
            var line = originalLines[i];
            var codeLine = StripGoComments(line, ref inBlockComment);
            var trimmed = codeLine.Trim();
            if (!inImportBlock)
            {
                if (codeLine.IndexOf("import", StringComparison.Ordinal) >= 0
                    && codeLine.IndexOf('(') >= 0
                    && GoImportBlockStartRegex.IsMatch(codeLine))
                {
                    inImportBlock = !trimmed.Contains(')');
                }
                continue;
            }

            if (trimmed.StartsWith(')'))
            {
                inImportBlock = false;
                continue;
            }

            if (codeLine.IndexOf('"') >= 0 && GoImportBlockEntryRegex.IsMatch(codeLine))
            {
                result ??= new bool[originalLines.Count];
                result[i] = true;
            }
            if (trimmed.Contains(')'))
                inImportBlock = false;
        }

        return result;
    }

    private static string StripGoComments(string line, ref bool inBlockComment)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= line.ToCharArray())[index] = ' ';

        void MaskRange(int start)
        {
            var masked = chars ??= line.ToCharArray();
            for (var index = start; index < line.Length; index++)
                masked[index] = ' ';
        }

        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                MaskAt(i);
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    MaskAt(++i);
                    inBlockComment = false;
                }
                continue;
            }

            if (line[i] is '"' or '`')
            {
                i = SkipGoStringLiteral(line, i);
                continue;
            }

            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                MaskRange(i);
                break;
            }

            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                MaskAt(i++);
                MaskAt(i);
                inBlockComment = true;
            }
        }

        return chars is null ? line : new string(chars);
    }

    private static int SkipGoStringLiteral(string line, int start)
    {
        var quote = line[start];
        var i = start + 1;
        while (i < line.Length)
        {
            if (quote == '"' && line[i] == '\\' && i + 1 < line.Length)
            {
                i += 2;
                continue;
            }

            if (line[i] == quote)
                return i;
            i++;
        }

        return line.Length;
    }

    private static void EmitGoTypeReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        bool isImportBlockLine)
    {
        var importMatch = !string.IsNullOrWhiteSpace(preparedLine)
            ? isImportBlockLine
                ? originalLine.IndexOf('"') >= 0
                    ? GoImportBlockEntryRegex.Match(originalLine)
                    : Match.Empty
                : originalLine.IndexOf("import", StringComparison.Ordinal) >= 0 && originalLine.IndexOf('"') >= 0
                    ? GoImportRegex.Match(originalLine)
                    : Match.Empty
            : Match.Empty;
        if (importMatch.Success)
        {
            var group = importMatch.Groups["name"];
            var aliasGroup = importMatch.Groups["alias"];
            if (aliasGroup.Success && aliasGroup.Value is not "." and not "_")
            {
                ReferenceExtractor.AddReference(references, seen, fileId, aliasGroup.Value, aliasGroup.Index, "type_reference", context, lineNumber, resolveContainerForColumn(aliasGroup.Index));
            }
            else
            {
                var packageName = LastPathSegment(group.Value);
                var packageOffset = group.Value.LastIndexOf(packageName, StringComparison.Ordinal);
                ReferenceExtractor.AddReference(references, seen, fileId, packageName, group.Index + Math.Max(0, packageOffset), "type_reference", context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }

        EmitGoTypeDeclarationParameterConstraints(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoTypeSpecTargetReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoInterfaceTypeSetTermReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoStandaloneTypeSetTermReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoSingleNameValueDeclarationTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoMultiNameValueDeclarationTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoMultiNameFieldDeclarationTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoSingleNameFieldDeclarationTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoEmbeddedFieldType(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoBuiltinTypeArgumentReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoChannelElementTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoTypeAssertionReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoTypeSwitchCaseReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoFunctionLiteralSignatureTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoFunctionTypeSignatureTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoInlineStructFieldTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoInlineInterfaceMemberTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoGenericCompositeLiteralReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoArraySliceCompositeLiteralTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoMapCompositeLiteralTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoCompositeTypeConversionReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoParenthesizedTypeConversionReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoMethodExpressionReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoGenericInstantiationTypeArgumentReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGoGenericCallTypeArgumentReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        foreach (var regex in GoTypeReferenceRegexes)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(regex, preparedLine))
            {
                var group = match.Groups["type"];
                EmitGoTypeExpression(group.Value, group.Index, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            }
        }

        if (preparedLine.IndexOf("func", StringComparison.Ordinal) >= 0
            && GoFuncRegex.IsMatch(preparedLine))
        {
            EmitGoFunctionSignatureTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
        else
        {
            EmitGoInterfaceMethodSignatureTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }

        if (preparedLine.IndexOf('{') >= 0 && ContainsGoUppercaseAscii(preparedLine))
        {
            foreach (Match match in Regex.EnumerateMatches(
                         GoCompositeLiteralRegex,
                         preparedLine))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    break;

                var group = match.Groups["name"];
                if (!IsGoCompositeLiteralContext(preparedLine, group.Index, group.Value.Length))
                    continue;

                ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "instantiate", context, lineNumber, resolveContainerForColumn(group.Index));
            }
        }
    }

    private static void EmitGoTypeDeclarationParameterConstraints(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var cursor = SkipWhitespace(line, 0);
        if (StartsWithKeyword(line, cursor, "type"))
            cursor = SkipWhitespace(line, cursor + "type".Length);

        if (cursor >= line.Length || !IsIdentifierStart(line[cursor]))
            return;

        cursor++;
        while (cursor < line.Length && IsSimpleIdentifierPart(line[cursor]))
            cursor++;

        cursor = SkipWhitespace(line, cursor);
        if (cursor >= line.Length || line[cursor] != '[')
            return;

        var close = ReferenceExtractor.FindMatchingChar(line, cursor, '[', ']');
        if (close < 0)
            return;

        var afterClose = SkipWhitespace(line, close + 1);
        if (afterClose >= line.Length)
            return;
        if (line[afterClose] == '=')
            afterClose = SkipWhitespace(line, afterClose + 1);
        if (afterClose >= line.Length || !IsGoTypeDeclarationBodyStart(line, afterClose))
            return;

        EmitGoTypeParameterConstraints(line, cursor, close + 1, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoTypeSpecTargetReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var cursor = SkipWhitespace(line, 0);
        if (!StartsWithKeyword(line, cursor, "type"))
            return;

        cursor = SkipWhitespace(line, cursor + "type".Length);
        if (cursor >= line.Length || !IsIdentifierStart(line[cursor]))
            return;

        cursor++;
        while (cursor < line.Length && IsSimpleIdentifierPart(line[cursor]))
            cursor++;

        cursor = SkipWhitespace(line, cursor);
        if (cursor < line.Length && line[cursor] == '[')
        {
            var close = ReferenceExtractor.FindMatchingChar(line, cursor, '[', ']');
            if (close < 0)
                return;
            cursor = SkipWhitespace(line, close + 1);
        }

        if (cursor < line.Length && line[cursor] == '=')
            cursor = SkipWhitespace(line, cursor + 1);

        if (cursor >= line.Length
            || StartsWithKeyword(line, cursor, "struct")
            || StartsWithKeyword(line, cursor, "interface")
            || !IsGoTypeExpressionStart(line, cursor))
        {
            return;
        }

        var typeEnd = FindGoInlineTypeExpressionEnd(line, cursor);
        if (typeEnd <= cursor)
            return;

        EmitGoTypeExpression(line[cursor..typeEnd], cursor, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoMultiNameValueDeclarationTypes(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var cursor = SkipWhitespace(line, 0);
        if (StartsWithKeyword(line, cursor, "var"))
            cursor = SkipWhitespace(line, cursor + "var".Length);
        else if (StartsWithKeyword(line, cursor, "const"))
            cursor = SkipWhitespace(line, cursor + "const".Length);

        if (!TryReadGoIdentifierList(line, ref cursor, requireComma: true))
            return;

        var typeStart = SkipWhitespace(line, cursor);
        if (typeStart >= line.Length || line[typeStart] == '=' || line.AsSpan(typeStart).StartsWith(":=", StringComparison.Ordinal))
            return;

        var typeEnd = typeStart;
        while (typeEnd < line.Length && line[typeEnd] != '=' && line[typeEnd] != '{')
            typeEnd++;

        EmitGoTypeExpression(line, typeStart, typeEnd, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoSingleNameValueDeclarationTypes(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var cursor = SkipWhitespace(line, 0);
        if (StartsWithKeyword(line, cursor, "var"))
            cursor = SkipWhitespace(line, cursor + "var".Length);
        else if (StartsWithKeyword(line, cursor, "const"))
            cursor = SkipWhitespace(line, cursor + "const".Length);
        else
            return;

        if (cursor >= line.Length || !IsIdentifierStart(line[cursor]))
            return;

        cursor++;
        while (cursor < line.Length && IsSimpleIdentifierPart(line[cursor]))
            cursor++;

        var afterName = SkipWhitespace(line, cursor);
        if (afterName < line.Length && line[afterName] == ',')
            return;

        var typeStart = afterName;
        if (typeStart >= line.Length || line[typeStart] == '=' || line.AsSpan(typeStart).StartsWith(":=", StringComparison.Ordinal))
            return;
        if (!IsGoTypeExpressionStart(line, typeStart))
            return;

        var typeEnd = FindGoInlineTypeExpressionEnd(line, typeStart);
        if (typeEnd <= typeStart)
            return;

        EmitGoTypeExpression(line, typeStart, typeEnd, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoMultiNameFieldDeclarationTypes(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var cursor = SkipWhitespace(line, 0);
        if (cursor >= line.Length || !IsIdentifierStart(line[cursor]))
            return;

        var nameStart = cursor;
        cursor++;
        while (cursor < line.Length && IsSimpleIdentifierPart(line[cursor]))
            cursor++;

        if (IsGoStatementKeyword(line[nameStart..cursor]))
            return;

        cursor = nameStart;
        if (!TryReadGoIdentifierList(line, ref cursor, requireComma: true))
            return;

        var typeStart = SkipWhitespace(line, cursor);
        if (typeStart >= line.Length || line[typeStart] is ':' or '=' || !IsGoTypeExpressionStart(line, typeStart))
            return;

        var typeEnd = FindGoInlineTypeExpressionEnd(line, typeStart);
        if (typeEnd <= typeStart)
            return;

        var afterType = SkipWhitespace(line, typeEnd);
        if (afterType < line.Length && line[afterType] != '`')
            return;

        EmitGoTypeExpression(line[typeStart..typeEnd], typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoSingleNameFieldDeclarationTypes(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var cursor = SkipWhitespace(line, 0);
        if (cursor >= line.Length || !IsIdentifierStart(line[cursor]))
            return;

        var nameStart = cursor;
        cursor++;
        while (cursor < line.Length && IsSimpleIdentifierPart(line[cursor]))
            cursor++;

        if (IsGoStatementKeyword(line[nameStart..cursor]))
            return;

        var typeStart = SkipWhitespace(line, cursor);
        if (typeStart >= line.Length || line[typeStart] is ':' or '=' or '(' || !IsGoTypeExpressionStart(line, typeStart))
            return;
        if (!IsLikelyGoFieldDeclarationTypeStart(line, typeStart))
            return;

        var typeEnd = FindGoInlineTypeExpressionEnd(line, typeStart);
        if (typeEnd <= typeStart)
            return;

        var afterType = SkipWhitespace(line, typeEnd);
        if (afterType < line.Length && line[afterType] != '`')
            return;

        EmitGoTypeExpression(line[typeStart..typeEnd], typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static bool IsLikelyGoFieldDeclarationTypeStart(string line, int typeStart)
    {
        if (typeStart >= line.Length || line[typeStart] != '[')
            return true;

        var close = ReferenceExtractor.FindMatchingChar(line, typeStart, '[', ']');
        if (close < 0)
            return false;

        var elementStart = SkipWhitespace(line, close + 1);
        return elementStart < line.Length && IsGoTypeExpressionStart(line, elementStart);
    }

    private static void EmitGoInterfaceTypeSetTermReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (!line.Contains('|'))
            return;

        var first = SkipWhitespace(line, 0);
        if (first >= line.Length || line.Contains(":=", StringComparison.Ordinal) || line.Contains('='))
            return;
        if (IsIdentifierStart(line[first]))
        {
            var nameEnd = first + 1;
            while (nameEnd < line.Length && IsSimpleIdentifierPart(line[nameEnd]))
                nameEnd++;
            if (IsGoStatementKeyword(line[first..nameEnd]))
                return;
        }

        foreach (var (termStart, termLength) in SplitGoTypeSetTermSpans(line))
        {
            var rawTerm = line.Substring(termStart, termLength);
            var term = rawTerm.Trim();
            if (term.Length == 0)
                continue;

            var tildeOffset = term[0] == '~' ? 1 : 0;
            while (tildeOffset < term.Length && char.IsWhiteSpace(term[tildeOffset]))
                tildeOffset++;
            if (tildeOffset >= term.Length || !IsGoTypeExpressionStart(term, tildeOffset))
                continue;

            var expression = term[tildeOffset..];
            if (!ContainsLikelyGoTypeArgument(expression))
                continue;

            var termTrimStart = rawTerm.IndexOf(term, StringComparison.Ordinal);
            EmitGoTypeExpression(expression, termStart + Math.Max(0, termTrimStart) + tildeOffset, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }
    }

    private static List<(int Start, int Length)> SplitGoTypeSetTermSpans(string line)
    {
        var spans = new List<(int Start, int Length)>(4);
        var termStart = 0;
        var squareDepth = 0;
        var parenDepth = 0;
        for (var cursor = 0; cursor < line.Length; cursor++)
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
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '|':
                    if (squareDepth == 0 && parenDepth == 0)
                    {
                        spans.Add((termStart, cursor - termStart));
                        termStart = cursor + 1;
                    }
                    break;
            }
        }

        spans.Add((termStart, line.Length - termStart));
        return spans;
    }

    private static void EmitGoStandaloneTypeSetTermReferences(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (line.Contains('|'))
            return;

        var cursor = SkipWhitespace(line, 0);
        if (cursor >= line.Length || line[cursor] != '~')
            return;

        var typeStart = SkipWhitespace(line, cursor + 1);
        if (typeStart >= line.Length || !IsGoTypeExpressionStart(line, typeStart))
            return;

        EmitGoTypeExpression(line, typeStart, line.Length, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static bool TryReadGoIdentifierList(string line, ref int cursor, bool requireComma)
    {
        var count = 0;
        while (cursor < line.Length)
        {
            cursor = SkipWhitespace(line, cursor);
            if (cursor >= line.Length || !IsIdentifierStart(line[cursor]))
                break;

            cursor++;
            while (cursor < line.Length && IsSimpleIdentifierPart(line[cursor]))
                cursor++;

            count++;
            var afterName = SkipWhitespace(line, cursor);
            if (afterName >= line.Length || line[afterName] != ',')
            {
                cursor = afterName;
                break;
            }

            cursor = afterName + 1;
        }

        return requireComma ? count > 1 : count > 0;
    }

    private static void EmitGoEmbeddedFieldType(
        string line,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var cursor = SkipWhitespace(line, 0);
        if (cursor >= line.Length)
            return;

        if (line[cursor] == '*')
            cursor = SkipWhitespace(line, cursor + 1);

        if (cursor >= line.Length || !IsIdentifierStart(line[cursor]))
            return;

        var nameStart = cursor;
        cursor++;
        while (cursor < line.Length && IsSimpleIdentifierPart(line[cursor]))
            cursor++;

        if (IsGoStatementKeyword(line[nameStart..cursor]))
            return;

        if (cursor < line.Length && line[cursor] == '.')
        {
            cursor++;
            if (cursor >= line.Length || !IsIdentifierStart(line[cursor]))
                return;
            cursor++;
            while (cursor < line.Length && IsSimpleIdentifierPart(line[cursor]))
                cursor++;
        }

        if (cursor < line.Length && line[cursor] == '[')
        {
            var close = ReferenceExtractor.FindMatchingChar(line, cursor, '[', ']');
            if (close < 0)
                return;
            cursor = close + 1;
        }

        var afterType = SkipWhitespace(line, cursor);
        if (afterType < line.Length && line[afterType] != '`')
            return;

        var typeStart = line.IndexOf('*') >= 0 && line.IndexOf('*') < nameStart
            ? line.IndexOf('*')
            : nameStart;
        EmitGoTypeExpression(line[typeStart..cursor], typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

}
