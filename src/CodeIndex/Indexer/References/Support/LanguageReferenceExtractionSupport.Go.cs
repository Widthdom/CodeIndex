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

    public static bool[] BuildGoImportBlockLineMap(IReadOnlyList<string> originalLines)
    {
        var result = new bool[originalLines.Count];
        var inImportBlock = false;
        var inBlockComment = false;

        for (var i = 0; i < originalLines.Count; i++)
        {
            var line = originalLines[i];
            var codeLine = StripGoComments(line, ref inBlockComment);
            var trimmed = codeLine.Trim();
            if (!inImportBlock)
            {
                if (GoImportBlockStartRegex.IsMatch(codeLine))
                    inImportBlock = !trimmed.Contains(')');
                continue;
            }

            if (trimmed.StartsWith(')'))
            {
                inImportBlock = false;
                continue;
            }

            result[i] = GoImportBlockEntryRegex.IsMatch(codeLine);
            if (trimmed.Contains(')'))
                inImportBlock = false;
        }

        return result;
    }

    private static string StripGoComments(string line, ref bool inBlockComment)
    {
        var chars = line.ToCharArray();
        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                chars[i] = ' ';
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    chars[++i] = ' ';
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
                for (; i < chars.Length; i++)
                    chars[i] = ' ';
                break;
            }

            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                chars[i++] = ' ';
                chars[i] = ' ';
                inBlockComment = true;
            }
        }

        return new string(chars);
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        bool isImportBlockLine)
    {
        var importMatch = !string.IsNullOrWhiteSpace(preparedLine)
            ? isImportBlockLine
                ? GoImportBlockEntryRegex.Match(originalLine)
                : GoImportRegex.Match(originalLine)
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

        if (GoFuncRegex.IsMatch(preparedLine))
            EmitGoFunctionSignatureTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        else
            EmitGoInterfaceMethodSignatureTypes(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        foreach (Match match in GoCompositeLiteralRegex.Matches(preparedLine))
        {
            var group = match.Groups["name"];
            if (!IsGoCompositeLiteralContext(preparedLine, group.Index, group.Value.Length))
                continue;

            ReferenceExtractor.AddReference(references, seen, fileId, group.Value, group.Index, "instantiate", context, lineNumber, resolveContainerForColumn(group.Index));
        }
    }

    private static void EmitGoTypeDeclarationParameterConstraints(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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

        var expression = line[typeStart..typeEnd].TrimEnd();
        EmitGoTypeExpression(expression, typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoSingleNameValueDeclarationTypes(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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

        var expression = line[typeStart..typeEnd].TrimEnd();
        EmitGoTypeExpression(expression, typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoMultiNameFieldDeclarationTypes(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        var spans = new List<(int Start, int Length)>();
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
        HashSet<string> seen,
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

        var expression = line[typeStart..].TrimEnd();
        if (expression.Length == 0)
            return;

        EmitGoTypeExpression(expression, typeStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
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
        HashSet<string> seen,
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

    private static void EmitGoBuiltinTypeArgumentReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
            var firstArgument = ReferenceExtractor.SplitTopLevelCommaSpans(argumentList).FirstOrDefault();
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
        HashSet<string> seen,
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var match = GoTypeSwitchCaseRegex.Match(line);
        if (!match.Success)
            return;

        var group = match.Groups["types"];
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(group.Value))
        {
            var rawType = group.Value.Substring(segmentStart, segmentLength);
            var expression = rawType.Trim();
            if (expression.Length == 0
                || expression is "nil" or "default"
                || !IsLikelyGoTypeSwitchCaseType(expression))
            {
                continue;
            }

            var trimStart = rawType.IndexOf(expression, StringComparison.Ordinal);
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (GoFuncRegex.IsMatch(line))
            return;

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

            var typeArguments = line[(open + 1)..close];
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeArguments))
            {
                var rawArgument = typeArguments.Substring(segmentStart, segmentLength);
                var expression = rawArgument.Trim();
                if (expression.Length == 0 || !ContainsLikelyGoTypeArgument(expression))
                    continue;

                var trimStart = rawArgument.IndexOf(expression, StringComparison.Ordinal);
                var absoluteStart = open + 1 + segmentStart + Math.Max(0, trimStart);
                EmitGoTypeExpression(expression, absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            }
        }
    }

    private static void EmitGoFunctionTypeSignatureTypes(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        HashSet<string> seen,
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

            var typeArguments = line[(open + 1)..close];
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeArguments))
            {
                var rawArgument = typeArguments.Substring(segmentStart, segmentLength);
                var expression = rawArgument.Trim();
                if (expression.Length == 0 || !ContainsLikelyGoTypeArgument(expression))
                    continue;

                var trimStart = rawArgument.IndexOf(expression, StringComparison.Ordinal);
                var absoluteStart = open + 1 + segmentStart + Math.Max(0, trimStart);
                EmitGoTypeExpression(expression, absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
            }
        }
    }

    private static void EmitGoInlineStructFieldTypeReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        var spans = new List<(int Start, int Length)>();
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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

        var expression = line[returnStart..end].TrimEnd();
        EmitGoTypeExpression(expression, returnStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoMapCompositeLiteralTypeReferences(
        string line,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (GoFuncRegex.IsMatch(line))
            return;

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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var typeArguments = line[(open + 1)..close];
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeArguments))
        {
            var rawArgument = typeArguments.Substring(segmentStart, segmentLength);
            var expression = rawArgument.Trim();
            if (expression.Length == 0 || !ContainsLikelyGoTypeArgument(expression))
                continue;

            var trimStart = rawArgument.IndexOf(expression, StringComparison.Ordinal);
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

    internal static void EmitGoBranchLabelReferences(string preparedLine, Action<string, int> addCallLikeReference)
    {
        foreach (Match match in GoBranchLabelRegex.Matches(preparedLine))
            addCallLikeReference(match.Groups["name"].Value, match.Groups["name"].Index);
    }

    private static void EmitGoFunctionSignatureTypes(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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

        var expression = preparedLine[returnStart..returnEnd].TrimEnd();
        EmitGoTypeExpression(expression, returnStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoInterfaceMethodSignatureTypes(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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
        HashSet<string> seen,
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

        var expression = preparedLine[returnStart..returnEnd].TrimEnd();
        EmitGoTypeExpression(expression, returnStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoTypeParameterConstraints(
        string line,
        int searchStart,
        int searchEnd,
        List<ReferenceRecord> references,
        HashSet<string> seen,
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

        var list = line[(open + 1)..close];
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var rawSegment = list.Substring(segmentStart, segmentLength);
            var fragment = rawSegment.Trim();
            if (fragment.Length == 0)
                continue;

            var constraintStart = FirstGoTypeParameterConstraintStart(fragment);
            if (constraintStart < 0)
                continue;

            var fragmentTrimStart = rawSegment.IndexOf(fragment, StringComparison.Ordinal);
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
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (end <= start)
            return;

        var list = line[start..end];
        var pendingSingleExpressions = new List<(string Expression, int AbsoluteStart)>();
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(list))
        {
            var rawSegment = list.Substring(segmentStart, segmentLength);
            var fragment = rawSegment.Trim();
            if (fragment.Length == 0)
                continue;

            var fragmentTrimStart = rawSegment.IndexOf(fragment, StringComparison.Ordinal);
            var absoluteFragmentStart = start + segmentStart + Math.Max(0, fragmentTrimStart);
            var typeStartInFragment = LastWhitespaceSeparatedTokenStart(fragment);
            if (typeStartInFragment < 0)
                continue;
            if (typeStartInFragment == 0)
            {
                pendingSingleExpressions.Add((fragment, absoluteFragmentStart));
                continue;
            }

            pendingSingleExpressions.Clear();
            var expression = fragment[typeStartInFragment..];
            var absoluteStart = absoluteFragmentStart + typeStartInFragment;
            EmitGoTypeExpression(expression, absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        }

        foreach (var (expression, absoluteStart) in pendingSingleExpressions)
            EmitGoTypeExpression(expression, absoluteStart, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitGoTypeExpression(
        string expression,
        int start,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var normalized = expression.Trim();
        var leading = expression.IndexOf(normalized, StringComparison.Ordinal);
        if (normalized.Length == 0)
            return;
        var absoluteStart = start + Math.Max(0, leading);
        ReferenceExtractor.AddTypeExpressionSegments(references, seen, fileId, normalized, absoluteStart, context, lineNumber, resolveContainerForColumn(absoluteStart), "go");
    }
}
