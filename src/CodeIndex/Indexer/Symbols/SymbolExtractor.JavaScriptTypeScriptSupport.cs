using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptBareMethods(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns,
        Func<string[]> getSanitizedLines)
    {
        var existingClassTargets = GetJavaScriptTypeScriptExistingClassScanTargets(lang, lines, symbols);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, existingClassTargets);

        var syntheticClassTargets = CollectJavaScriptTypeScriptSyntheticClassScanTargets(fileId, lang, lines, symbols, getPrivateScopeColumns);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, syntheticClassTargets);

        var objectLiteralTargets = CollectJavaScriptTypeScriptObjectLiteralScanTargets(lang, lines, getPrivateScopeColumns);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, objectLiteralTargets);
        ExtractJavaScriptTypeScriptExportSurfaceSymbols(fileId, lang, lines, symbols, getPrivateScopeColumns, getSanitizedLines, objectLiteralTargets);
        ExtractJavaScriptTypeScriptQualifiedAssignments(fileId, lang, lines, symbols, getPrivateScopeColumns, getSanitizedLines);
    }

    // Scans for object literal declarations (`const obj = { ... }`, `module.exports = { ... }`
    // etc.) and builds class-body scan targets with ContainerKind="object". The class-body
    // scanner already handles method shorthand (`name()`, `get/set name()`, `*name()`,
    // `async name()`), so routing object literals through the same scanner picks up those
    // members without a separate pass. Nested function/class scopes are skipped via
    // privateScopeColumns so method bodies don't leak inner-object methods back to the top level.
    // `const obj = { ... }` や `module.exports = { ... }` 等のオブジェクトリテラル宣言を走査し、
    // ContainerKind="object" のクラスボディ用スキャンターゲットを構築する。クラスボディスキャナは
    // 既に method shorthand (`name()`, `get/set name()`, `*name()`, `async name()`) を扱うため、
    // 同じスキャナ経由でオブジェクトリテラルのメンバを抽出できる。ネストされた function/class
    // スコープは privateScopeColumns で弾き、内側のオブジェクトメンバをトップレベルに漏らさない。
    private static List<JavaScriptClassScanTarget> CollectJavaScriptTypeScriptObjectLiteralScanTargets(
        string lang,
        string[] lines,
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns)
    {
        if (!LinesContain(lines, '{'))
            return [];

        var privateScopeColumns = getPrivateScopeColumns();
        List<JavaScriptClassScanTarget>? targets = null;
        HashSet<(int StartIndex, int ScanStartIndex, int ScanEndExclusive, string ContainerName)>? targetIdentities = null;
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var bindingMatch = JavaScriptTypeScriptObjectLiteralBindingRegex.Match(sanitizedLine);
            Match? exportDefaultMatch = null;
            if (!bindingMatch.Success)
            {
                var edm = JavaScriptTypeScriptExportDefaultObjectLiteralRegex.Match(sanitizedLine);
                if (!edm.Success)
                    continue;
                exportDefaultMatch = edm;
            }
            var match = exportDefaultMatch ?? bindingMatch;
            var isExportDefault = exportDefaultMatch != null;

            // Skip declarations nested inside a function/class body, and — for non-exported
            // const/let bindings — also inside block scopes or namespace scopes. The object
            // literal itself may be legitimate, but its method-shorthand members are already
            // reachable via the enclosing scope, and emitting them would leak non-public names
            // to the top level. `var` stays function-scoped so block-scope skip is not applied;
            // `module.exports` / `exports.X` / `export const` / `export default` are treated as
            // exported and kept.
            // function/class 本体内のネストした宣言はスキップする。加えて非 export の const/let は
            // ブロックスコープや namespace スコープも private 扱いにする。var は function スコープのため
            // ブロックスコープは除外せず、module.exports / exports.X / export const / export default は
            // export 扱いで維持する。
            var includeBlockScope = !isExportDefault
                && bindingMatch.Groups["bindingKind"].Success
                && bindingMatch.Groups["bindingKind"].Value is "const" or "let";
            if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, match.Index, sanitizedLine, includeBlockScope))
                continue;

            var isExported = isExportDefault
                || TryGetGroup(bindingMatch, "visibility") == "export"
                || bindingMatch.Groups["exportsAlias"].Success
                || bindingMatch.Groups["moduleExportsAlias"].Success
                || bindingMatch.Groups["bracketName"].Success
                || bindingMatch.Groups["moduleExports"].Success;
            if (!isExported
                && IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, match.Index, sanitizedLine))
            {
                continue;
            }

            if (!TryFindJavaScriptTypeScriptObjectLiteralOpenBrace(
                    lines,
                    i,
                    match.Index + match.Length,
                    sanitizedLine,
                    lexState,
                    out var openBraceLineIndex,
                    out var openBraceColumn))
            {
                continue;
            }

            var (_, bodyStartLine, bodyEndLine) = ResolveRange(lines, openBraceLineIndex, BodyStyle.Brace, lang, openBraceColumn);
            if (bodyStartLine == null || bodyEndLine == null)
                continue;

            var containerName = isExportDefault
                ? "default"
                : (TryGetGroup(bindingMatch, "alias")
                    ?? TryGetGroup(bindingMatch, "exportsAlias")
                    ?? TryGetGroup(bindingMatch, "moduleExportsAlias")
                    ?? (bindingMatch.Groups["moduleExports"].Success ? "module.exports" : null)
                    ?? "object");

            var candidate = CreateJavaScriptClassScanTarget(
                lines,
                lang,
                i,
                match.Index,
                bodyStartLine,
                bodyEndLine,
                containerKind: "object",
                containerName: containerName,
                isExported: isExported);

            var targetIdentity = (candidate.StartIndex, candidate.ScanStartIndex, candidate.ScanEndExclusive, candidate.ContainerName);
            if ((targetIdentities ??= []).Add(targetIdentity))
                (targets ??= []).Add(candidate);
        }

        if (targets is null)
            return [];

        SortJavaScriptTypeScriptClassScanTargets(targets);
        return targets;
    }

    private static void ExtractJavaScriptTypeScriptExportSurfaceSymbols(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns,
        Func<string[]> getSanitizedLines,
        List<JavaScriptClassScanTarget> objectLiteralTargets)
    {
        if (!LinesContain(lines, "export", StringComparison.Ordinal))
            return;

        var privateScopeColumns = getPrivateScopeColumns();
        var sanitizedLines = getSanitizedLines();
        ExtractJavaScriptTypeScriptReExportSymbols(fileId, lang, lines, sanitizedLines, symbols);
        ExtractJavaScriptTypeScriptLocalNamedExportSymbols(fileId, lines, sanitizedLines, symbols);
        ExtractJavaScriptTypeScriptExportedVariableSymbols(fileId, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptDefaultExportArrowFunctionSymbols(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptDestructuredNamedExports(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsNamedExportAssignments(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsDefaultFunctionAssignments(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsDefinePropertyExports(fileId, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsDefinePropertiesExports(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsObjectAssignExports(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptExportedObjectLiteralProperties(fileId, lines, sanitizedLines, symbols, objectLiteralTargets);
    }

    private static string[] BuildJavaScriptTypeScriptSanitizedLines(string[] lines)
    {
        string[]? sanitizedLines = null;
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            if (sanitizedLines != null)
            {
                sanitizedLines[i] = lexedLine.SanitizedLine;
            }
            else if (!ReferenceEquals(lexedLine.SanitizedLine, lines[i]))
            {
                sanitizedLines = (string[])lines.Clone();
                sanitizedLines[i] = lexedLine.SanitizedLine;
            }

            lexState = lexedLine.EndState;
        }

        return sanitizedLines ?? lines;
    }

    private static bool TryCollectJavaScriptTypeScriptAssignedRhs(
        string[] rawLines,
        string[] sanitizedLines,
        int assignmentLineIndex,
        int assignmentStartColumn,
        int sameLineRhsColumn,
        string lang,
        out string rhs,
        out int rhsStartLineIndex,
        out int rhsStartColumn,
        out int rhsEndLineIndex,
        out int rhsEndColumn,
        out string signature)
    {
        rhs = string.Empty;
        rhsStartLineIndex = assignmentLineIndex;
        rhsStartColumn = sameLineRhsColumn;
        rhsEndLineIndex = assignmentLineIndex;
        rhsEndColumn = -1;
        signature = string.Empty;

        var builderCapacity = EstimateJavaScriptTypeScriptStatementCapacity(sanitizedLines, assignmentLineIndex);
        var rhsBuilder = new System.Text.StringBuilder(builderCapacity);
        var signatureBuilder = new System.Text.StringBuilder(builderCapacity);
        var pendingWrapperParenClose = false;

        for (int lineIndex = assignmentLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var column = lineIndex == assignmentLineIndex
                ? Math.Max(0, sameLineRhsColumn)
                : 0;

            if (!TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(sanitizedLines, ref lineIndex, ref column))
                continue;

            var sanitizedLine = sanitizedLines[lineIndex];
            while (sanitizedLines[lineIndex][column] == '('
                && HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(sanitizedLines[lineIndex], column))
            {
                column++;
                pendingWrapperParenClose = true;
                if (!TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(sanitizedLines, ref lineIndex, ref column))
                    return false;

                sanitizedLine = sanitizedLines[lineIndex];
            }

            if (pendingWrapperParenClose && column < sanitizedLine.Length && sanitizedLine[column] == ')')
            {
                column++;
                pendingWrapperParenClose = false;
            }

            var statementEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, column, lang);
            var sliceEndExclusive = statementEndColumn >= column
                ? statementEndColumn + 1
                : sanitizedLine.Length;

            var rhsStartSliceColumn = Math.Min(column, sanitizedLine.Length);
            var statementSliceEndColumn = Math.Min(sliceEndExclusive, sanitizedLine.Length);
            var rhsSlice = rhsStartSliceColumn < statementSliceEndColumn
                ? sanitizedLine[rhsStartSliceColumn..statementSliceEndColumn].TrimEnd()
                : string.Empty;
            if (rhsSlice.Length > 0)
            {
                if (rhsBuilder.Length == 0)
                {
                    rhsStartLineIndex = lineIndex;
                    rhsStartColumn = rhsStartSliceColumn;
                }

                if (rhsBuilder.Length > 0)
                    rhsBuilder.Append(' ');
                rhsBuilder.Append(rhsSlice);
            }

            var signatureSlice = lineIndex == assignmentLineIndex
                ? rawLines[lineIndex][Math.Min(assignmentStartColumn, rawLines[lineIndex].Length)..Math.Min(rawLines[lineIndex].Length, statementSliceEndColumn)].Trim()
                : rawLines[lineIndex].Trim();
            if (signatureSlice.Length > 0)
            {
                if (signatureBuilder.Length > 0)
                    signatureBuilder.Append(' ');
                signatureBuilder.Append(signatureSlice);
            }

            if (statementEndColumn >= column)
            {
                rhsEndLineIndex = lineIndex;
                rhsEndColumn = statementEndColumn;
                rhs = rhsBuilder.ToString().Trim();
                signature = signatureBuilder.ToString().Trim();
                return true;
            }
        }

        if (rhsBuilder.Length > 0)
        {
            rhs = rhsBuilder.ToString().Trim();
            signature = signatureBuilder.ToString().Trim();
            rhsEndLineIndex = Math.Max(assignmentLineIndex, sanitizedLines.Length - 1);
            rhsEndColumn = sanitizedLines[rhsEndLineIndex].Length - 1;
            return true;
        }

        rhs = string.Empty;
        signature = string.Empty;
        return false;
    }

    private static bool TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(string[] sanitizedLines, ref int lineIndex, ref int column)
    {
        while (lineIndex < sanitizedLines.Length)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                column++;

            if (column < sanitizedLine.Length)
                return true;

            lineIndex++;
            column = 0;
        }

        return false;
    }

    private static bool TryFindJavaScriptTypeScriptAssignedRhsStart(
        string[] sanitizedLines,
        int assignmentLineIndex,
        int sameLineRhsColumn,
        out int startLineIndex,
        out int startColumn)
    {
        for (int lineIndex = assignmentLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == assignmentLineIndex
                ? Math.Max(0, sameLineRhsColumn)
                : 0;

            while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                column++;

            if (column >= sanitizedLine.Length)
                continue;

            if (sanitizedLine[column] == '('
                && HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(sanitizedLine, column))
            {
                continue;
            }

            if (sanitizedLine[column] == ')')
            {
                var remainder = sanitizedLine[column..].Trim();
                if (remainder.Length == 0 || remainder == ")" || remainder == ");")
                    continue;
            }

            startLineIndex = lineIndex;
            startColumn = column;
            return true;
        }

        startLineIndex = assignmentLineIndex;
        startColumn = sameLineRhsColumn;
        return false;
    }

    private static bool TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
        string[] rawLines,
        int startLineIndex,
        int startColumn,
        string? lang,
        out int openBraceLineIndex,
        out int openBraceColumn)
    {
        openBraceLineIndex = -1;
        openBraceColumn = -1;

        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var awaitingFunctionBody = false;
        var awaitingArrowBody = false;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var lexState = new JavaScriptLexState();

        for (int lineIndex = startLineIndex; lineIndex < rawLines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(rawLines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var column = lineIndex == startLineIndex
                ? Math.Max(0, startColumn)
                : 0;

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                var wasFunctionHeaderActive = functionHeaderState.Active;

                if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                        tokenEnd++;

                    if (sanitizedLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                    ref functionHeaderState,
                    sanitizedLine,
                    column,
                    lang ?? "javascript",
                    out var functionHeaderAdvanceColumns);
                if (wasFunctionHeaderActive && !functionHeaderState.Active)
                    awaitingFunctionBody = true;

                if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                {
                    column += functionHeaderAdvanceColumns;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                    continue;

                if (awaitingFunctionBody)
                {
                    if (ch == '{')
                    {
                        openBraceLineIndex = lineIndex;
                        openBraceColumn = column;
                        return true;
                    }

                    return false;
                }

                if (awaitingArrowBody)
                {
                    if (ch == '{')
                    {
                        openBraceLineIndex = lineIndex;
                        openBraceColumn = column;
                        return true;
                    }

                    return false;
                }

                if (ch == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    continue;
                }

                if (lang == "typescript" && ch == '<' && parenDepth == 0 && bracketDepth == 0)
                {
                    angleDepth++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0 && (column == 0 || sanitizedLine[column - 1] != '='))
                {
                    angleDepth--;
                    continue;
                }

                if (ch == '='
                    && column + 1 < sanitizedLine.Length
                    && sanitizedLine[column + 1] == '>'
                    && parenDepth == 0
                    && bracketDepth == 0
                    && angleDepth == 0)
                {
                    awaitingArrowBody = true;
                    column++;
                }
            }
        }

        return false;
    }

    private static bool HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(string sanitizedLine, int startColumn)
    {
        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (char.IsWhiteSpace(ch) || ch == '(')
                continue;

            return false;
        }

        return true;
    }

    private static bool TrySkipJavaScriptTypeScriptNonIdentifierObjectLiteralKey(string sanitizedLine, ref int index)
    {
        var probe = index;
        if (TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref probe, out _)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
        {
            while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
                probe++;

            if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ':')
                return false;

            index = probe + 1;
            return true;
        }

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != '[')
            return false;

        var bracketDepth = 1;
        probe++;
        while (probe < sanitizedLine.Length && bracketDepth > 0)
        {
            if (sanitizedLine[probe] == '[')
            {
                bracketDepth++;
            }
            else if (sanitizedLine[probe] == ']')
            {
                bracketDepth--;
            }

            probe++;
        }

        if (bracketDepth != 0)
            return false;

        while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
            probe++;

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ':')
            return false;

        index = probe + 1;
        return true;
    }

    private static string TrimJavaScriptTypeScriptQuotedModuleName(string moduleName)
    {
        if (moduleName.Length >= 2
            && moduleName[0] == moduleName[^1]
            && (moduleName[0] == '\'' || moduleName[0] == '"'))
        {
            return moduleName[1..^1];
        }

        return moduleName;
    }

    private static bool TryExtractJavaScriptTypeScriptReExportModuleName(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int endLineIndex,
        int startColumn,
        bool waitForClosedSpecifierList,
        out string moduleName)
    {
        moduleName = string.Empty;
        var braceDepth = 0;
        var sawOpeningBrace = !waitForClosedSpecifierList;

        for (int lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (waitForClosedSpecifierList)
                {
                    if (ch == '{')
                    {
                        braceDepth++;
                        sawOpeningBrace = true;
                        continue;
                    }

                    if (!sawOpeningBrace)
                        continue;

                    if (ch == '}' && braceDepth > 0)
                    {
                        braceDepth--;
                        continue;
                    }

                    if (braceDepth > 0)
                        continue;
                }

                if (!IsJavaScriptTypeScriptKeywordAt(sanitizedLine, column, "from"))
                    continue;

                if (!TryFindJavaScriptTypeScriptReExportModuleQuote(rawLines, sanitizedLines, lineIndex, endLineIndex, column + "from".Length, out var quoteLineIndex, out var quoteColumn))
                    return false;

                var rawLine = rawLines[quoteLineIndex];
                var quoteChar = rawLine[quoteColumn];
                var closeQuoteColumn = rawLine.IndexOf(quoteChar, quoteColumn + 1);
                if (closeQuoteColumn <= quoteColumn)
                    return false;

                moduleName = TrimJavaScriptTypeScriptQuotedModuleName(rawLine[quoteColumn..(closeQuoteColumn + 1)]);
                return moduleName.Length > 0;
            }
        }

        return false;
    }

    private static bool TryFindJavaScriptTypeScriptReExportModuleQuote(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int endLineIndex,
        int startColumn,
        out int quoteLineIndex,
        out int quoteColumn)
    {
        quoteLineIndex = -1;
        quoteColumn = -1;

        for (int lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? startColumn : 0;
            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                    continue;

                if (ch is '\'' or '"')
                {
                    quoteLineIndex = lineIndex;
                    quoteColumn = column;
                    return column < rawLines[lineIndex].Length;
                }

                return false;
            }
        }

        return false;
    }

    private static bool IsJavaScriptTypeScriptKeywordAt(string text, int index, string keyword)
        => IsJavaScriptTypeScriptKeywordAt(text.AsSpan(), index, keyword);

    private static bool IsJavaScriptTypeScriptKeywordAt(ReadOnlySpan<char> text, int index, string keyword)
    {
        if (index < 0
            || index + keyword.Length > text.Length
            || !text[index..(index + keyword.Length)].SequenceEqual(keyword.AsSpan()))
        {
            return false;
        }

        var before = index > 0 ? text[index - 1] : '\0';
        if (char.IsLetterOrDigit(before) || before is '_' or '$')
            return false;

        var afterIndex = index + keyword.Length;
        if (afterIndex >= text.Length)
            return true;

        var after = text[afterIndex];
        return !(char.IsLetterOrDigit(after) || after is '_' or '$');
    }

    private static ReadOnlySpan<char> SkipJavaScriptTypeScriptTypeOnlyExportModifier(ReadOnlySpan<char> exportRemainder)
    {
        if (IsJavaScriptTypeScriptKeywordAt(exportRemainder, 0, "type"))
            return exportRemainder["type".Length..].TrimStart();

        return exportRemainder;
    }

    private static int FindJavaScriptTypeScriptBalancedGenericListEnd(string text, int startIndex)
    {
        if (startIndex < 0
            || startIndex >= text.Length
            || text[startIndex] != '<')
        {
            return -1;
        }

        var depth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (int index = startIndex; index < text.Length; index++)
        {
            var ch = text[index];
            switch (ch)
            {
                case '<':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        depth++;
                    break;
                case '>':
                    if (parenDepth == 0
                        && bracketDepth == 0
                        && braceDepth == 0
                        && depth > 0
                        && (index == 0 || text[index - 1] != '='))
                    {
                        depth--;
                        if (depth == 0)
                            return index;
                    }
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
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptBalancedDelimiterEnd(string text, int startIndex, char openChar, char closeChar)
    {
        if (startIndex < 0
            || startIndex >= text.Length
            || text[startIndex] != openChar)
        {
            return -1;
        }

        var depth = 0;
        for (int index = startIndex; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == openChar)
            {
                depth++;
            }
            else if (ch == closeChar)
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return -1;
    }

    private static int ReadJavaScriptTypeScriptIdentifierLength(string text, int startIndex)
    {
        if (startIndex < 0 || startIndex >= text.Length)
            return 0;

        var first = text[startIndex];
        if (!(char.IsLetter(first) || first is '_' or '$'))
            return 0;

        var index = startIndex + 1;
        while (index < text.Length)
        {
            var ch = text[index];
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '$'))
                break;

            index++;
        }

        return index - startIndex;
    }

    private static IEnumerable<string> ParseJavaScriptTypeScriptReExportedNames(string specifierList)
    {
        foreach (var rawSpecifier in SplitJavaScriptTypeScriptExportSpecifiers(specifierList))
        {
            var specifier = rawSpecifier.Trim();
            if (specifier.Length == 0)
                continue;

            if (specifier.StartsWith("type ", StringComparison.Ordinal))
                specifier = TrimJavaScriptTypeScriptStart(specifier, "type ".Length);

            var asIndex = specifier.LastIndexOf(" as ", StringComparison.Ordinal);
            var exportedName = asIndex >= 0
                ? specifier[(asIndex + " as ".Length)..].Trim()
                : specifier;
            exportedName = NormalizeJavaScriptTypeScriptExportedSpecifierName(exportedName);
            if (exportedName.Length == 0)
                continue;

            yield return exportedName;
        }
    }

    private static IEnumerable<string> SplitJavaScriptTypeScriptExportSpecifiers(string specifierList)
    {
        var start = 0;
        var quote = '\0';
        var escapeNext = false;

        for (var index = 0; index < specifierList.Length; index++)
        {
            var ch = specifierList[index];
            if (quote != '\0')
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == ',')
            {
                yield return specifierList[start..index];
                start = index + 1;
            }
        }

        yield return specifierList[start..];
    }

    private static string StripJavaScriptTypeScriptSpecifierComments(string specifiers)
    {
        var builder = new StringBuilder(specifiers.Length);
        var quote = '\0';
        var escapeNext = false;

        for (var index = 0; index < specifiers.Length; index++)
        {
            var ch = specifiers[index];
            var next = index + 1 < specifiers.Length ? specifiers[index + 1] : '\0';

            if (quote != '\0')
            {
                builder.Append(ch);
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                builder.Append(ch);
                continue;
            }

            if (ch == '/' && next == '/')
            {
                index += 2;
                while (index < specifiers.Length && specifiers[index] != '\n')
                    index++;
                if (index < specifiers.Length)
                    builder.Append('\n');
                continue;
            }

            if (ch == '/' && next == '*')
            {
                index += 2;
                while (index + 1 < specifiers.Length && (specifiers[index] != '*' || specifiers[index + 1] != '/'))
                    index++;
                if (index + 1 < specifiers.Length)
                    index++;
                builder.Append(' ');
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool TryExtractJavaScriptTypeScriptExportSpecifierListFromSignature(string signature, out string specifiers)
    {
        specifiers = string.Empty;

        var openBraceIndex = signature.IndexOf('{', StringComparison.Ordinal);
        if (openBraceIndex < 0)
            return false;

        var quote = '\0';
        var escapeNext = false;
        for (var index = openBraceIndex + 1; index < signature.Length; index++)
        {
            var ch = signature[index];
            if (quote != '\0')
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '}')
            {
                specifiers = signature[(openBraceIndex + 1)..index];
                return true;
            }
        }

        return false;
    }

    private static bool ContainsJavaScriptTypeScriptStringLiteralSpecifierName(string specifiers)
        => specifiers.IndexOf('"', StringComparison.Ordinal) >= 0
            || specifiers.IndexOf('\'', StringComparison.Ordinal) >= 0;

    private static string NormalizeJavaScriptTypeScriptExportedSpecifierName(string exportedName)
    {
        var trimmedName = exportedName.AsSpan().Trim();
        if (trimmedName.Length < 2 || trimmedName[0] is not ('\'' or '"'))
            return MaterializeTrimmedJavaScriptTypeScriptSpecifierName(exportedName, trimmedName);

        var quote = trimmedName[0];
        if (trimmedName[^1] != quote)
            return MaterializeTrimmedJavaScriptTypeScriptSpecifierName(exportedName, trimmedName);

        var builder = new StringBuilder(Math.Max(0, trimmedName.Length - 2));
        for (var index = 1; index < trimmedName.Length - 1; index++)
        {
            var ch = trimmedName[index];
            if (ch == '\\' && index + 1 < trimmedName.Length - 1)
            {
                builder.Append(trimmedName[index + 1]);
                index++;
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string MaterializeTrimmedJavaScriptTypeScriptSpecifierName(string original, ReadOnlySpan<char> trimmed)
        => trimmed.Length == original.Length ? original : trimmed.ToString();

    // Scans forward from (`startLineIndex`, `startColumn`) through the lex-sanitized source for
    // the first `{`, hopping across lines when only whitespace (including newlines) remains. The
    // passed `sanitizedStartLine` is the already-sanitized version of lines[startLineIndex] and
    // `lineEndState` is the lexer state AFTER that line. Any non-whitespace, non-`{` character
    // aborts the scan (returns false) so we don't misclassify arbitrary RHS expressions as object
    // literals. Strings / comments stay masked because we drive the scan through LexJavaScriptLine.
    // (`startLineIndex`, `startColumn`) から lex sanitized のソースを前方に走査し、最初の `{` を探す。
    // 空白 (改行を含む) だけなら行を跨いで続行する。`sanitizedStartLine` は lines[startLineIndex] の
    // sanitized 版で、`lineEndState` はそのライン終了時の lexer state。`{` 以外の非空白文字が現れた時点で
    // 走査を打ち切る (false を返す) ので、オブジェクトリテラルでない右辺を誤って拾わない。
    // LexJavaScriptLine を介するため、文字列・コメントは常にマスクされた状態で判定できる。
    private static bool TryFindJavaScriptTypeScriptObjectLiteralOpenBrace(
        string[] lines,
        int startLineIndex,
        int startColumn,
        string sanitizedStartLine,
        JavaScriptLexState lineEndState,
        out int openBraceLineIndex,
        out int openBraceColumn)
    {
        openBraceLineIndex = -1;
        openBraceColumn = -1;

        for (int c = Math.Max(0, startColumn); c < sanitizedStartLine.Length; c++)
        {
            var ch = sanitizedStartLine[c];
            if (char.IsWhiteSpace(ch))
                continue;
            if (ch == '{')
            {
                openBraceLineIndex = startLineIndex;
                openBraceColumn = c;
                return true;
            }
            return false;
        }

        var lexState = lineEndState;
        for (int li = startLineIndex + 1; li < lines.Length; li++)
        {
            var lexed = LexJavaScriptLine(lines[li], lexState);
            lexState = lexed.EndState;
            var nextSan = lexed.SanitizedLine;
            for (int c = 0; c < nextSan.Length; c++)
            {
                var ch = nextSan[c];
                if (char.IsWhiteSpace(ch))
                    continue;
                if (ch == '{')
                {
                    openBraceLineIndex = li;
                    openBraceColumn = c;
                    return true;
                }
                return false;
            }
        }

        return false;
    }

    private static List<JavaScriptClassScanTarget> GetJavaScriptTypeScriptExistingClassScanTargets(string lang, string[] lines, List<SymbolRecord> symbols)
    {
        List<(SymbolRecord Symbol, int OriginalIndex)>? classSymbols = null;
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (symbol.Kind is "class" or "interface" && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
                (classSymbols ??= []).Add((symbol, index));
        }

        if (classSymbols is not { Count: > 0 })
            return [];

        if (classSymbols.Count == 1)
        {
            var symbol = classSymbols[0].Symbol;
            return
            [
                CreateJavaScriptClassScanTarget(
                    lines,
                    lang,
                    symbol.StartLine - 1,
                    FindJavaScriptTypeScriptSymbolStartColumn(lines[symbol.StartLine - 1], symbol.Signature),
                    symbol.BodyStartLine,
                    symbol.BodyEndLine,
                    symbol.Kind,
                    symbol.Name),
            ];
        }

        classSymbols.Sort(CompareJavaScriptTypeScriptClassSymbolEntries);

        var targets = new List<JavaScriptClassScanTarget>(classSymbols.Count);
        foreach (var entry in classSymbols)
        {
            var symbol = entry.Symbol;
            targets.Add(CreateJavaScriptClassScanTarget(
                lines,
                lang,
                symbol.StartLine - 1,
                FindJavaScriptTypeScriptSymbolStartColumn(lines[symbol.StartLine - 1], symbol.Signature),
                symbol.BodyStartLine,
                symbol.BodyEndLine,
                symbol.Kind,
                symbol.Name));
        }

        return targets;
    }

    private static List<JavaScriptClassScanTarget> CollectJavaScriptTypeScriptSyntheticClassScanTargets(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns)
    {
        if (!LinesContain(lines, "class", StringComparison.Ordinal))
            return [];

        var privateScopeColumns = getPrivateScopeColumns();
        List<JavaScriptClassScanTarget>? targets = null;
        HashSet<SymbolLineIdentity>? symbolLineIdentities = null;
        HashSet<(int StartIndex, int StartColumn, int ScanStartIndex, int ScanEndExclusive, int FirstLineScanOffset, string ContainerKind, string ContainerName)>? targetIdentities = null;
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var lineOffset = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (lineOffset >= 0 && lineOffset < sanitizedLine.Length)
            {
                TryAddJavaScriptTypeScriptSyntheticClassTarget(fileId, lang, lines, symbols, ref targets, ref symbolLineIdentities, ref targetIdentities, i, lineOffset, sanitizedLine, privateScopeColumns);
                lineOffset = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, lineOffset + 1);
            }
        }

        if (targets is null)
            return [];

        SortJavaScriptTypeScriptClassScanTargets(targets);
        return targets;
    }

    private static void SortJavaScriptTypeScriptClassScanTargets(List<JavaScriptClassScanTarget> targets)
    {
        if (targets.Count < 2)
            return;

        var entries = new List<(JavaScriptClassScanTarget Target, int OriginalIndex)>(targets.Count);
        for (var index = 0; index < targets.Count; index++)
            entries.Add((targets[index], index));

        entries.Sort(CompareJavaScriptTypeScriptClassScanTargetEntries);
        for (var index = 0; index < entries.Count; index++)
            targets[index] = entries[index].Target;
    }

    private static int CompareJavaScriptTypeScriptClassSymbolEntries(
        (SymbolRecord Symbol, int OriginalIndex) left,
        (SymbolRecord Symbol, int OriginalIndex) right)
    {
        var startLineComparison = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
        if (startLineComparison != 0)
            return startLineComparison;

        var endLineComparison = right.Symbol.EndLine.CompareTo(left.Symbol.EndLine);
        return endLineComparison != 0
            ? endLineComparison
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static int CompareJavaScriptTypeScriptClassScanTargetEntries(
        (JavaScriptClassScanTarget Target, int OriginalIndex) left,
        (JavaScriptClassScanTarget Target, int OriginalIndex) right)
    {
        var startIndexComparison = left.Target.StartIndex.CompareTo(right.Target.StartIndex);
        if (startIndexComparison != 0)
            return startIndexComparison;

        var scanEndComparison = right.Target.ScanEndExclusive.CompareTo(left.Target.ScanEndExclusive);
        return scanEndComparison != 0
            ? scanEndComparison
            : left.OriginalIndex.CompareTo(right.OriginalIndex);
    }

    private static JavaScriptScopePrivacyFlags GetJavaScriptTypeScriptPrivacyFlags(Stack<JavaScriptScopeKind> scopeStack, bool arrowExpressionActive)
    {
        var flags = JavaScriptScopePrivacyFlags.None;
        if (arrowExpressionActive)
            flags |= JavaScriptScopePrivacyFlags.FunctionLike;

        foreach (var scopeKind in scopeStack)
        {
            if (scopeKind is JavaScriptScopeKind.Function or JavaScriptScopeKind.StaticBlock)
                flags |= JavaScriptScopePrivacyFlags.FunctionLike;
            else if (scopeKind == JavaScriptScopeKind.Block)
                flags |= JavaScriptScopePrivacyFlags.Block;
            else if (scopeKind == JavaScriptScopeKind.Namespace)
                flags |= JavaScriptScopePrivacyFlags.Namespace;

            if (flags == (JavaScriptScopePrivacyFlags.FunctionLike | JavaScriptScopePrivacyFlags.Block | JavaScriptScopePrivacyFlags.Namespace))
                break;
        }

        return flags;
    }

    private static bool IsInsideJavaScriptTypeScriptMethodContainer(Stack<JavaScriptScopeKind> scopeStack)
    {
        return scopeStack.Count > 0 && scopeStack.Peek() is JavaScriptScopeKind.Class or JavaScriptScopeKind.Object;
    }

    private static void BeginJavaScriptTypeScriptFunctionHeader(ref JavaScriptTypeScriptFunctionHeaderState state)
    {
        state = new JavaScriptTypeScriptFunctionHeaderState
        {
            Active = true,
        };
    }

    private static JavaScriptTypeScriptFunctionHeaderConsumeResult ConsumeJavaScriptTypeScriptFunctionHeaderChar(
        ref JavaScriptTypeScriptFunctionHeaderState state,
        string sanitizedLine,
        int column,
        string lang,
        out int advanceColumns)
    {
        advanceColumns = 0;
        if (!state.Active)
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.NotActive;

        var ch = sanitizedLine[column];
        if (char.IsWhiteSpace(ch))
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;

        if (state.InReturnType)
        {
            if (ch == ';'
                && state.ReturnParenDepth == 0
                && state.ReturnBracketDepth == 0
                && state.ReturnAngleDepth == 0
                && state.ReturnBraceDepth == 0)
            {
                state = default;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '(')
            {
                state.ReturnParenDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "(";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == ')' && state.ReturnParenDepth > 0)
            {
                state.ReturnParenDepth--;
                state.PreviousReturnToken = ")";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '[')
            {
                state.ReturnBracketDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "[";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == ']' && state.ReturnBracketDepth > 0)
            {
                state.ReturnBracketDepth--;
                state.PreviousReturnToken = "]";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '<')
            {
                state.ReturnAngleDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "<";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '>' && state.ReturnAngleDepth > 0)
            {
                state.ReturnAngleDepth--;
                state.PreviousReturnToken = ">";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '{')
            {
                if (state.ReturnParenDepth == 0
                    && state.ReturnBracketDepth == 0
                    && state.ReturnAngleDepth == 0
                    && state.ReturnBraceDepth == 0)
                {
                    if (CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(state.PreviousReturnToken))
                    {
                        state.ReturnBraceDepth++;
                        state.ReturnSawToken = true;
                        state.PreviousReturnToken = "{";
                        return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
                    }

                    if (state.ReturnSawToken)
                    {
                        state = default;
                        return JavaScriptTypeScriptFunctionHeaderConsumeResult.BodyStart;
                    }
                }

                state.ReturnBraceDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "{";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '}' && state.ReturnBraceDepth > 0)
            {
                state.ReturnBraceDepth--;
                state.PreviousReturnToken = "}";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch is '?' or ':' or '|' or '&' or ',')
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = ch.ToString();
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "=>";
                advanceColumns = 1;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            var returnTypeIndex = column;
            if (TrySkipTypeScriptTypeToken(sanitizedLine, ref returnTypeIndex, out var returnTypeToken))
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = returnTypeToken;
                advanceColumns = returnTypeIndex - column - 1;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            state.ReturnSawToken = true;
            state.PreviousReturnToken = ch.ToString();
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (lang == "typescript" && state.SawParameterList && ch == ':')
        {
            state.InReturnType = true;
            state.ReturnParenDepth = 0;
            state.ReturnBracketDepth = 0;
            state.ReturnAngleDepth = 0;
            state.ReturnBraceDepth = 0;
            state.ReturnSawToken = false;
            state.PreviousReturnToken = ":";
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '(')
        {
            state.ParenDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ')' && state.ParenDepth > 0)
        {
            state.ParenDepth--;
            if (state.ParenDepth == 0 && state.BracketDepth == 0 && state.BraceDepth == 0)
                state.SawParameterList = true;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '[' && (state.ParenDepth > 0 || state.BracketDepth > 0 || state.BraceDepth > 0 || !state.SawParameterList))
        {
            state.BracketDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ']' && state.BracketDepth > 0)
        {
            state.BracketDepth--;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '{')
        {
            if (state.SawParameterList && state.ParenDepth == 0 && state.BracketDepth == 0 && state.BraceDepth == 0)
            {
                state = default;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.BodyStart;
            }

            state.BraceDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '}' && state.BraceDepth > 0)
        {
            state.BraceDepth--;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ';')
        {
            state = default;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        var tokenIndex = column;
        if (TrySkipTypeScriptTypeToken(sanitizedLine, ref tokenIndex, out _))
        {
            advanceColumns = tokenIndex - column - 1;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
    }

    private static void AdvanceJavaScriptTypeScriptFieldInitializerState(
        ref bool inFieldInitializer,
        ref int initializerParenDepth,
        ref int initializerBracketDepth,
        ref int initializerBraceDepth,
        char ch)
    {
        if (ch == '(')
        {
            initializerParenDepth++;
            return;
        }

        if (ch == ')' && initializerParenDepth > 0)
        {
            initializerParenDepth--;
            return;
        }

        if (ch == '[')
        {
            initializerBracketDepth++;
            return;
        }

        if (ch == ']' && initializerBracketDepth > 0)
        {
            initializerBracketDepth--;
            return;
        }

        if (ch == '{')
        {
            initializerBraceDepth++;
            return;
        }

        if (ch == '}' && initializerBraceDepth > 0)
        {
            initializerBraceDepth--;
            return;
        }

        if (ch == ';'
            && initializerParenDepth == 0
            && initializerBracketDepth == 0
            && initializerBraceDepth == 0)
        {
            inFieldInitializer = false;
        }
    }

    private static bool ShouldContinueJavaScriptTypeScriptFieldInitializer(ReadOnlySpan<char> continuationInput, string? lang)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < continuationInput.Length && char.IsWhiteSpace(continuationInput[firstNonWhitespace]))
            firstNonWhitespace++;

        if (firstNonWhitespace >= continuationInput.Length)
            return true;

        var remainingInput = continuationInput[firstNonWhitespace..].ToString();
        if (IsJavaScriptTypeScriptMethodCandidateStart(remainingInput, 0))
        {
            var matchCandidate = lang == "typescript"
                ? NormalizeTypeScriptBareMethodMatchInput(remainingInput)
                : remainingInput;
            if (TryParseJavaScriptTypeScriptMethodHeader(matchCandidate, 0, lang, out _))
                return false;
        }

        return StartsJavaScriptTypeScriptExpressionContinuation(remainingInput);
    }

    private static bool CanStartJavaScriptTypeScriptClassFieldInitializer(string sanitizedLine, int index)
    {
        if (index < 0 || index >= sanitizedLine.Length || sanitizedLine[index] != '=')
            return false;

        return index + 1 >= sanitizedLine.Length || sanitizedLine[index + 1] != '>';
    }

    private static bool IsJavaScriptTypeScriptMethodCandidateStart(string sanitizedLine, int index)
    {
        if (index < 0 || index >= sanitizedLine.Length)
            return false;

        var ch = sanitizedLine[index];
        if (ch != '#'
            && ch != '@'
            && ch != '*'
            && ch != '['
            && ch != '\''
            && ch != '"'
            && !char.IsDigit(ch)
            && !IsJavaScriptTypeScriptIdentifierStart(ch))
            return false;

        return index == 0 || !IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index - 1]);
    }

    private static JavaScriptLexedLine LexJavaScriptLine(string line, JavaScriptLexState state)
    {
        char[]? sanitized = null;
        var i = 0;

        char[] GetSanitizedBuffer()
        {
            return sanitized ??= line.ToCharArray();
        }

        void SetSanitized(int index, char value)
        {
            if (sanitized is null)
            {
                if (line[index] == value)
                    return;

                sanitized = line.ToCharArray();
            }

            sanitized[index] = value;
        }

        while (i < line.Length)
        {
            var ch = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (state.Mode == JavaScriptLexMode.BlockComment)
            {
                SetSanitized(i, ' ');
                if (ch == '*' && next == '/')
                {
                    SetSanitized(i + 1, ' ');
                    state = state with { Mode = JavaScriptLexMode.Code };
                    i++;
                }

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.SingleQuote)
            {
                if (ch is not '\'' and not '\\')
                    SetSanitized(i, ' ');

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '\'')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.DoubleQuote)
            {
                if (ch is not '"' and not '\\')
                    SetSanitized(i, ' ');

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '"')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.TemplateString)
            {
                if (ch is not '`' and not '\\')
                    SetSanitized(i, ' ');

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '`')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (ch == '/' && next == '/')
            {
                while (i < line.Length)
                {
                    SetSanitized(i, ' ');
                    i++;
                }

                break;
            }

            if (ch == '/' && next == '*')
            {
                SetSanitized(i, ' ');
                SetSanitized(i + 1, ' ');
                state = state with { Mode = JavaScriptLexMode.BlockComment };
                i++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (state.ExpectingControlFlowOpenParen && ch != '(')
                state = state with { ExpectingControlFlowOpenParen = false };

            if (state.RegexAllowedAfterControlFlowParen && ch != '/')
            {
                state = state with
                {
                    RegexAllowedAfterControlFlowParen = false
                };
            }

            if (ch == '\'')
            {
                state = state with { Mode = JavaScriptLexMode.SingleQuote, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '"')
            {
                state = state with { Mode = JavaScriptLexMode.DoubleQuote, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '`')
            {
                state = state with { Mode = JavaScriptLexMode.TemplateString, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '/' && CanStartJavaScriptRegexLiteral(state))
            {
                SetSanitized(i, ' ');
                i = SkipJavaScriptRegexLiteral(line, GetSanitizedBuffer(), i);
                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Other,
                    PreviousIdentifier = null
                };
                i++;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_' || ch == '$')
            {
                var tokenStart = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '$'))
                    i++;

                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Identifier,
                    PreviousIdentifier = line[tokenStart..i],
                    ExpectingControlFlowOpenParen = IsJavaScriptControlFlowKeyword(line[tokenStart..i])
                };
                continue;
            }

            if (char.IsDigit(ch))
            {
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '.'))
                    i++;

                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Number,
                    PreviousIdentifier = null,
                    ExpectingControlFlowOpenParen = false
                };
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
                var controlFlowParenDepth = state.ControlFlowParenDepth;
                var regexAllowedAfterControlFlowParen = state.RegexAllowedAfterControlFlowParen;

                if (ch == '(')
                {
                    if (state.ExpectingControlFlowOpenParen)
                    {
                        controlFlowParenDepth = 1;
                        regexAllowedAfterControlFlowParen = false;
                    }
                    else if (controlFlowParenDepth > 0)
                    {
                        controlFlowParenDepth++;
                    }
                }
                else if (ch == ')' && controlFlowParenDepth > 0)
                {
                    controlFlowParenDepth--;
                    if (controlFlowParenDepth == 0)
                        regexAllowedAfterControlFlowParen = true;
                }

                state = state with
                {
                    PreviousTokenKind = ch switch
                    {
                        ')' => JavaScriptPrevTokenKind.CloseParen,
                        ']' => JavaScriptPrevTokenKind.CloseBracket,
                        '}' => JavaScriptPrevTokenKind.CloseBrace,
                        _ => JavaScriptPrevTokenKind.Other
                    },
                    PreviousIdentifier = null,
                    ExpectingControlFlowOpenParen = false,
                    ControlFlowParenDepth = controlFlowParenDepth,
                    RegexAllowedAfterControlFlowParen = regexAllowedAfterControlFlowParen
                };
            }

            i++;
        }

        return new JavaScriptLexedLine(sanitized is null ? line : new string(sanitized), state);
    }

    // Sanitize a contiguous block of C# source lines for cross-line structural
    // analysis (attribute boundaries, bracket depth). String / char / comment
    // content is blanked to spaces while preserving original line lengths, and
    // the lexer state (VerbatimString / RawString / BlockComment / ...) is
    // threaded across line boundaries so multi-line literals no longer leak
    // stray `[` / `]` / `"` characters into downstream parsers.
    // After `LexCSharpLine` sanitization, string delimiters themselves (`"`,
    // `'`, `\`) are also blanked so continuation lines (e.g. `]")] decl` closing
    // a verbatim string from the previous physical line) do not look like they
    // open a fresh string literal when the caller scans them line-by-line.
    // C# ソース行の塊を、横断的な構造解析（属性境界や bracket depth）向けに
    // sanitize する。文字列 / 文字 / コメント内容は空白で置換し元の行長を保持、
    // lexer state（VerbatimString / RawString / BlockComment など）を行をまたいで
    // 持ち越すことで、複数行リテラル由来の `[` / `]` / `"` が下流パーサへ漏れない。
    // `LexCSharpLine` による sanitize 後、文字列区切りそのもの（`"`, `'`, `\`）も
    // 空白化する。こうしないと、前行の verbatim 文字列を閉じる継続行
    // （例: `]")] decl`）が単独で走査された際に新たな文字列リテラル開始と

    private static bool CanStartJavaScriptRegexLiteral(JavaScriptLexState state)
    {
        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.None)
            return true;

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.Other)
            return true;

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.Identifier)
        {
            return IsJavaScriptRegexPrefixKeyword(state.PreviousIdentifier);
        }

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.CloseParen)
            return state.RegexAllowedAfterControlFlowParen;

        return false;
    }

    private static bool IsJavaScriptControlFlowKeyword(string identifier)
    {
        return identifier is "if" or "for" or "while" or "switch" or "catch" or "with";
    }

    private static bool IsJavaScriptRegexPrefixKeyword(string? identifier)
    {
        return identifier is
            "return" or "throw" or "case" or "delete" or "typeof" or "void" or "new" or
            "in" or "of" or "instanceof" or "yield" or "await" or "else" or "do" or "finally";
    }

    private static int SkipJavaScriptRegexLiteral(string line, char[] sanitized, int slashIndex)
    {
        var i = slashIndex + 1;
        var inCharacterClass = false;

        while (i < line.Length)
        {
            sanitized[i] = ' ';
            var ch = line[i];
            if (ch == '\\')
            {
                if (i + 1 < line.Length)
                {
                    sanitized[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                return i;
            }

            if (ch == '[')
            {
                inCharacterClass = true;
                i++;
                continue;
            }

            if (ch == ']' && inCharacterClass)
            {
                inCharacterClass = false;
                i++;
                continue;
            }

            if (ch == '/' && !inCharacterClass)
            {
                i++;
                while (i < line.Length && char.IsLetter(line[i]))
                {
                    sanitized[i] = ' ';
                    i++;
                }

                return i - 1;
            }

            i++;
        }

        return line.Length - 1;
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindJavaScriptBraceRange(string[] lines, int startIndex, string? lang, int startColumn = 0)
    {
        var depth = 0;
        var opened = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var pendingArrowBody = false;
        var arrowExpressionActive = false;
        var arrowExpressionParenDepth = 0;
        var arrowExpressionBracketDepth = 0;
        var arrowExpressionBraceDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        int? bodyStartLine = null;
        var lexState = new JavaScriptLexState();

        for (int i = startIndex; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var effectiveStartColumn = startColumn;
            if (i == startIndex
                && startColumn > 0
                && TryParseJavaScriptTypeScriptMethodHeader(lexedLine.SanitizedLine, startColumn, lang, out var methodHeader))
            {
                effectiveStartColumn = methodHeader.BodyStartColumn;
            }

            var scanLine = i == startIndex && effectiveStartColumn > 0 && effectiveStartColumn < lexedLine.SanitizedLine.Length
                ? lexedLine.SanitizedLine[effectiveStartColumn..]
                : i == startIndex && effectiveStartColumn >= lexedLine.SanitizedLine.Length
                    ? string.Empty
                    : lexedLine.SanitizedLine;

            if (arrowExpressionActive
                && arrowExpressionBraceDepth == 0
                && arrowExpressionParenDepth == 0
                && arrowExpressionBracketDepth == 0
                && !StartsJavaScriptTypeScriptExpressionContinuation(scanLine))
            {
                return (i, bodyStartLine ?? startIndex + 1, i);
            }

            for (int column = 0; column < scanLine.Length; column++)
            {
                var ch = scanLine[column];
                if (!opened
                    && !arrowExpressionActive
                    && !functionHeaderState.Active
                    && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < scanLine.Length && IsJavaScriptTypeScriptIdentifierPart(scanLine[tokenEnd]))
                        tokenEnd++;

                    if (scanLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                if (!opened && !arrowExpressionActive)
                {
                    var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                        ref functionHeaderState,
                        scanLine,
                        column,
                        lang ?? "javascript",
                        out var functionHeaderAdvanceColumns);
                    if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                    {
                        column += functionHeaderAdvanceColumns;
                        continue;
                    }
                }

                if (!opened && !arrowExpressionActive && i == startIndex && ch == '=' && column + 1 < scanLine.Length && scanLine[column + 1] == '>')
                {
                    pendingArrowBody = true;
                    column++;
                    continue;
                }

                if (pendingArrowBody)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;

                    bodyStartLine ??= i + 1;
                    if (ch == '{')
                        pendingArrowBody = false;
                    else
                    {
                        arrowExpressionActive = true;
                        pendingArrowBody = false;
                    }
                }

                if (arrowExpressionActive)
                {
                    if (ch == '(')
                    {
                        arrowExpressionParenDepth++;
                        continue;
                    }

                    if (ch == ')' && arrowExpressionParenDepth > 0)
                    {
                        arrowExpressionParenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        arrowExpressionBracketDepth++;
                        continue;
                    }

                    if (ch == ']' && arrowExpressionBracketDepth > 0)
                    {
                        arrowExpressionBracketDepth--;
                        continue;
                    }

                    if (ch == '{')
                    {
                        arrowExpressionBraceDepth++;
                        continue;
                    }

                    if (ch == '}' && arrowExpressionBraceDepth > 0)
                    {
                        arrowExpressionBraceDepth--;
                        continue;
                    }

                    if (ch == ';'
                        && arrowExpressionParenDepth == 0
                        && arrowExpressionBracketDepth == 0
                        && arrowExpressionBraceDepth == 0)
                    {
                        return (i + 1, bodyStartLine ?? startIndex + 1, i + 1);
                    }

                    continue;
                }

                if (!opened)
                {
                    if (ch == '(')
                    {
                        parenDepth++;
                        continue;
                    }

                    if (ch == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        bracketDepth++;
                        continue;
                    }

                    if (ch == ']' && bracketDepth > 0)
                    {
                        bracketDepth--;
                        continue;
                    }

                    if (ch == '<')
                    {
                        if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                            angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }
                }

                if (ch == '{')
                {
                    if (!opened && (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0))
                        continue;

                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                        return (i + 1, bodyStartLine, i + 1);
                }
            }

            if (!opened
                && !arrowExpressionActive
                && !functionHeaderState.Active
                && parenDepth == 0
                && bracketDepth == 0
                && angleDepth == 0
                && FindJavaScriptTypeScriptTrimmedEndExclusive(scanLine, scanLine.Length) is var trimmedEnd
                && trimmedEnd > 0
                && scanLine[trimmedEnd - 1] == ';')
                return (startIndex + 1, null, null);
        }

        if (arrowExpressionActive)
            return (lines.Length, bodyStartLine ?? startIndex + 1, lines.Length);

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    private static int FindJavaScriptBodyOpenBraceIndex(string[] lines, int startIndex, int bodyStartIndex, string? lang, int startColumn = 0)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var lexState = new JavaScriptLexState();

        for (int i = startIndex; i <= bodyStartIndex; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var initialColumn = i == startIndex ? Math.Max(0, startColumn) : 0;
            for (int column = initialColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                        tokenEnd++;

                    if (sanitizedLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                    ref functionHeaderState,
                    sanitizedLine,
                    column,
                    lang ?? "javascript",
                    out var functionHeaderAdvanceColumns);
                if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                {
                    column += functionHeaderAdvanceColumns;
                    continue;
                }

                if (!char.IsWhiteSpace(ch))
                {
                    if (ch == '(')
                    {
                        parenDepth++;
                        continue;
                    }

                    if (ch == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        bracketDepth++;
                        continue;
                    }

                    if (ch == ']' && bracketDepth > 0)
                    {
                        bracketDepth--;
                        continue;
                    }

                    if (ch == '<')
                    {
                        if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                            angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }
                }

                if (ch != '{')
                    continue;

                if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                    continue;

                return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptSameLineBodyEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        if (!TryParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out var methodHeader))
            return -1;

        return FindJavaScriptSameLineBraceBodyEndColumn(sanitizedLine, methodHeader.BodyStartColumn);
    }

    // Same-line body end finder for class-field arrow functions. The scanner already knows the
    // sanitized body-open column from the arrow capture, so we walk braces from that column
    // without re-parsing the header (which the method-header parser would reject).
    // クラスフィールドのアロー関数向けの同一行 body 終了列探索。スキャナが arrow capture の段階で
    // sanitized 上の body 開始列を把握しているので、ヘッダを再パースせずそこから brace を辿る。
    private static int FindJavaScriptSameLineArrowBodyEndColumn(string line, int bodyStartColumn)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        return FindJavaScriptSameLineBraceBodyEndColumn(sanitizedLine, bodyStartColumn);
    }

    private static int FindJavaScriptSameLineBraceBodyEndColumn(string sanitizedLine, int bodyStartColumn)
    {
        var depth = 0;
        var opened = false;

        for (int column = Math.Max(0, bodyStartColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptSymbolStartColumn(string line, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return 0;

        var startColumn = line.IndexOf(signature, StringComparison.Ordinal);
        return startColumn >= 0 ? startColumn : 0;
    }

    private static int FindJavaScriptTypeScriptSameLineBraceEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        var bodyStartColumn = FindJavaScriptTypeScriptBodyOpenBraceColumn(sanitizedLine, startColumn, lang);
        if (bodyStartColumn < 0)
            return -1;

        var depth = 0;
        var opened = false;

        for (int column = bodyStartColumn; column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptBodyOpenBraceColumn(string sanitizedLine, int startColumn, string? lang)
    {
        if (TryParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out var methodHeader))
            return methodHeader.BodyStartColumn;

        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
            {
                var tokenStart = column;
                var tokenEnd = column + 1;
                while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                    tokenEnd++;

                if (sanitizedLine[tokenStart..tokenEnd] == "function")
                {
                    BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                    column = tokenEnd - 1;
                    continue;
                }
            }

            var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                ref functionHeaderState,
                sanitizedLine,
                column,
                lang ?? "javascript",
                out var functionHeaderAdvanceColumns);
            if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
            {
                column += functionHeaderAdvanceColumns;
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
                if (ch == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    continue;
                }

                if (ch == '<')
                {
                    if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                        angleDepth++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0)
                {
                    angleDepth--;
                    continue;
                }
            }

            if (ch != '{')
                continue;

            if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                continue;

            return column;
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptSameLineStatementEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (char.IsWhiteSpace(ch))
                continue;

            switch (ch)
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
                case '<':
                    if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                        angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                        return column;
                    break;
            }
        }

        return -1;
    }

    internal static string NormalizeTypeScriptBareMethodMatchInput(string input)
    {
        if (!input.Contains('<', StringComparison.Ordinal) && !input.Contains('{', StringComparison.Ordinal))
            return input;

        if (!TryParseJavaScriptTypeScriptMethodHeader(input, 0, "typescript", out var methodHeader))
            return input;

        char[]? chars = null;
        if (methodHeader.GenericStartColumn != null && methodHeader.GenericEndColumn != null)
        {
            chars = input.ToCharArray();
            for (int replaceIndex = methodHeader.GenericStartColumn.Value; replaceIndex <= methodHeader.GenericEndColumn.Value; replaceIndex++)
                chars[replaceIndex] = ' ';
        }

        if (methodHeader.ReturnTypeStartColumn != null && methodHeader.ReturnTypeEndColumn != null)
        {
            for (int replaceIndex = methodHeader.ReturnTypeStartColumn.Value; replaceIndex <= methodHeader.ReturnTypeEndColumn.Value; replaceIndex++)
            {
                var ch = chars is null ? input[replaceIndex] : chars[replaceIndex];
                if (ch == '{')
                    (chars ??= input.ToCharArray())[replaceIndex] = '(';
                else if (ch == '}')
                    (chars ??= input.ToCharArray())[replaceIndex] = ')';
            }
        }

        return chars is null ? input : new string(chars);
    }

    // Class-field arrow like `handleClick = () => { ... }` is not matched by the method-header
    // parser because the identifier is followed by `=` instead of `(`. This parser handles that
    // shape (with optional TS modifiers, field type annotation, generics, and return type).
    // 正規表現や method-header パーサは `name = ... =>` 形式のクラスフィールド矢印関数を拾えないため、
    // 専用パーサでそのシェイプだけ（修飾子・フィールド型注釈・ジェネリクス・戻り値型を含む）をパースする。
    private static bool TryParseJavaScriptTypeScriptClassFieldArrowHeader(
        string sanitizedHeader,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderInfo arrowInfo)
    {
        arrowInfo = default;
        var index = Math.Max(0, startColumn);
        string? visibility = null;

        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(sanitizedHeader, ref index);

        string? candidateName = null;
        while (index < sanitizedHeader.Length)
        {
            if (!TryReadJavaScriptTypeScriptMethodToken(sanitizedHeader, ref index, out var token))
                return false;

            if (token == "*")
                return false;

            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;

            if (TypeScriptBareMethodModifiers.Contains(token)
                && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(sanitizedHeader, index))
            {
                // `get`/`set`/`async`/`abstract` as leading modifier here would turn the construct
                // back into a method (not an arrow field); bail so the method-header parser owns it.
                // `get`/`set`/`async`/`abstract` が先頭修飾子に来るケースは arrow field ではなく
                // method なので、method-header パーサ側に委ねるためここで諦める。
                if (token is "get" or "set" or "async" or "abstract")
                    return false;
                if (token is "public" or "private" or "protected")
                    visibility = token;
                continue;
            }

            candidateName = token;
            break;
        }

        if (candidateName == null)
            return false;

        if (index < sanitizedHeader.Length && (sanitizedHeader[index] == '?' || sanitizedHeader[index] == '!'))
        {
            index++;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == ':')
        {
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilFieldEquals(sanitizedHeader, ref index))
                return false;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != '=')
            return false;
        if (index + 1 < sanitizedHeader.Length && (sanitizedHeader[index + 1] == '=' || sanitizedHeader[index + 1] == '>'))
            return false;
        index++;
        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        if (index + 5 <= sanitizedHeader.Length
            && string.CompareOrdinal(sanitizedHeader, index, "async", 0, 5) == 0
            && (index + 5 == sanitizedHeader.Length || !IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[index + 5])))
        {
            index += 5;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        int? genericStartColumn = null;
        int? genericEndColumn = null;
        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == '<')
        {
            genericStartColumn = index;
            var angleDepth = 0;
            while (index < sanitizedHeader.Length)
            {
                var ch = sanitizedHeader[index];
                if (ch == '<')
                {
                    angleDepth++;
                }
                else if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
                {
                    index += 2;
                    continue;
                }
                else if (ch == '>')
                {
                    angleDepth--;
                    if (angleDepth == 0)
                    {
                        genericEndColumn = index;
                        index++;
                        break;
                    }
                }
                index++;
            }
            if (genericEndColumn == null)
                return false;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index >= sanitizedHeader.Length)
            return false;

        if (sanitizedHeader[index] == '(')
        {
            var parenDepth = 0;
            while (index < sanitizedHeader.Length)
            {
                var ch = sanitizedHeader[index];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        index++;
                        break;
                    }
                }
                index++;
            }
            if (parenDepth != 0)
                return false;
        }
        else if (IsJavaScriptTypeScriptIdentifierStart(sanitizedHeader[index]))
        {
            index++;
            while (index < sanitizedHeader.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[index]))
                index++;
        }
        else
        {
            return false;
        }

        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        int? returnTypeStartColumn = null;
        int? returnTypeEndColumn = null;
        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == ':')
        {
            returnTypeStartColumn = index;
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilArrow(sanitizedHeader, ref index, out var rtEnd))
                return false;
            returnTypeEndColumn = rtEnd;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index + 1 >= sanitizedHeader.Length
            || sanitizedHeader[index] != '='
            || sanitizedHeader[index + 1] != '>')
            return false;

        index += 2;
        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        if (index >= sanitizedHeader.Length)
            return false;

        // Block-body arrow (`=> { ... }`). HeaderEndColumn == BodyStartColumn, both point at `{`.
        // ブロック本体矢印 (`=> { ... }`)。header 終端と body 開始は同じ `{` を指す。
        if (sanitizedHeader[index] == '{')
        {
            arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                candidateName,
                index,
                visibility,
                genericStartColumn,
                genericEndColumn,
                returnTypeStartColumn,
                returnTypeEndColumn,
                index);
            return true;
        }

        // Expression-body arrow (`=> expr;`). Walk until a class-field terminator at depth 0.
        // Explicit `;` always terminates; implicit ASI also terminates when we hit the enclosing
        // class body `}` or a newline followed by a new class-member start (identifier+`=`/`(`,
        // `#private`, `*name`, decorator, or modifier keyword). `[` is treated as continuation
        // here because a bare `[` is ambiguous between computed-member access and a computed
        // method name; see StartsJavaScriptTypeScriptClassMemberAt for the full rationale.
        // `{}` / `()` / `[]` stay balanced; strings / comments are already masked by the upstream
        // lexer. If the accumulated header ends at depth 0 with expression tokens but no visible
        // terminator, return false so TryCapture pulls another line and retries.
        // 式本体矢印 (`=> expr;`)。深さ 0 でのクラスフィールド終端まで歩く。明示的な `;` は常に終端し、
        // 暗黙の ASI は囲みクラス body の `}` か、改行直後に新しいクラスメンバの開始 (identifier+`=`/`(`、
        // `#private`、`*name`、decorator、修飾子キーワード) が来た場合にも終端する。`[` は computed
        // member access の継続と computed method 名の両方になり得るためここでは継続扱いとする
        // (詳細は StartsJavaScriptTypeScriptClassMemberAt のコメント参照)。
        // 括弧類はバランスを取り、文字列・コメントは上流の lexer でマスク済み。終端が見えないまま
        // 蓄積ヘッダの末尾に達したら false を返し、TryCapture に次の行を積ませる。
        var expressionStart = index;
        var parenDepth2 = 0;
        var bracketDepth2 = 0;
        var braceDepth2 = 0;
        int? lastNonWhitespace = null;
        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == ';' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0)
            {
                if (lastNonWhitespace == null)
                    return false;
                arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                    candidateName,
                    expressionStart,
                    visibility,
                    genericStartColumn,
                    genericEndColumn,
                    returnTypeStartColumn,
                    returnTypeEndColumn,
                    expressionStart,
                    HasBody: true,
                    ExpressionBodyEndColumn: lastNonWhitespace);
                return true;
            }

            if (ch == '}' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0)
            {
                // Enclosing class body `}` at depth 0. If we already have expression tokens that
                // can validly end a statement (identifier/number/`)`/`]`/`}`), treat it as ASI and
                // emit. Otherwise bail so the class scanner handles the closer.
                // 囲みクラス body の `}` (深さ 0)。識別子/数値/`)`/`]`/`}` のように文末になり得るトークンが
                // 既に見えていれば ASI として終端扱いで emit する。無ければクラススキャナに委ねるため false。
                if (lastNonWhitespace != null
                    && CanJavaScriptTypeScriptExpressionEndAt(sanitizedHeader[lastNonWhitespace.Value]))
                {
                    arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                        candidateName,
                        expressionStart,
                        visibility,
                        genericStartColumn,
                        genericEndColumn,
                        returnTypeStartColumn,
                        returnTypeEndColumn,
                        expressionStart,
                        HasBody: true,
                        ExpressionBodyEndColumn: lastNonWhitespace);
                    return true;
                }
                return false;
            }

            if (ch == '\n' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0
                && lastNonWhitespace != null
                && CanJavaScriptTypeScriptExpressionEndAt(sanitizedHeader[lastNonWhitespace.Value]))
            {
                var peek = index + 1;
                while (peek < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[peek]))
                    peek++;
                // peek == sanitizedHeader.Length means we exhausted the accumulated header after
                // this newline — need more input from TryCapture. Break out of the heuristic and
                // fall through to the normal end-of-input `return false` path.
                // peek が末尾に達した場合は、この改行以降に蓄積ヘッダ上の文字が尽きたということなので
                // TryCapture に次の行を積ませる必要がある。ヒューリスティックは停止し、ループ末尾の
                // end-of-input `return false` に任せる。
                if (peek < sanitizedHeader.Length
                    && StartsJavaScriptTypeScriptClassMemberAt(sanitizedHeader, peek))
                {
                    arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                        candidateName,
                        expressionStart,
                        visibility,
                        genericStartColumn,
                        genericEndColumn,
                        returnTypeStartColumn,
                        returnTypeEndColumn,
                        expressionStart,
                        HasBody: true,
                        ExpressionBodyEndColumn: lastNonWhitespace);
                    return true;
                }
            }

            if (ch == '(') parenDepth2++;
            else if (ch == ')' && parenDepth2 > 0) parenDepth2--;
            else if (ch == '[') bracketDepth2++;
            else if (ch == ']' && bracketDepth2 > 0) bracketDepth2--;
            else if (ch == '{') braceDepth2++;
            else if (ch == '}' && braceDepth2 > 0) braceDepth2--;

            if (!char.IsWhiteSpace(ch))
                lastNonWhitespace = index;
            index++;
        }

        return false;
    }

    // Returns true when `ch` is a token that can validly end a JavaScript / TypeScript expression
    // (identifier/digit tail, closing bracket, `$`/`_`, or the closing delimiter of a string /
    // template literal). The upstream lexer preserves the opening and closing `"`/`'`/`` ` `` in
    // the sanitized header (only the body content is blanked to spaces), so a string-returning
    // arrow such as `only = () => "x"` ends with a visible quote character here.
    // Operator-like characters (`+`, `.`, `,`, etc.) return false so multi-line expression
    // continuations are not accidentally cut off by the ASI heuristic.
    // `ch` が JavaScript / TypeScript の式を終端できるトークン (識別子/数字末尾、閉じ括弧、`$`/`_`、
    // 文字列・テンプレートリテラルの閉じデリミタ) なら true。上流の lexer は sanitized header 上で
    // `"` / `'` / `` ` `` の開き/閉じ文字は残し、リテラル本体だけをスペースに blank する。
    // そのため `only = () => "x"` のような文字列を返す式は、ここでは閉じクォートが lastNonWhitespace と
    // して可視のまま残る。演算子類 (`+`、`.`、`,` 等) は false を返すことで、複数行の式継続が ASI
    // ヒューリスティックで誤って途中終端されないようにする。
    private static bool CanJavaScriptTypeScriptExpressionEndAt(char ch)
    {
        if (char.IsLetterOrDigit(ch))
            return true;
        return ch is '_' or '$' or ')' or ']' or '}' or '"' or '\'' or '`';
    }

    // Returns true when the position starts a new class-body member declaration: `}` (class body
    // close), `;` (stray empty statement), `#` / `@` / `*<name>` lead tokens, or an identifier that
    // is either a well-known class-member modifier keyword or is followed by a class-field /
    // method-shorthand syntactic marker (`=`, `(`, `<`, `?`, `!`, `:`, `;`).
    // Note: `[` is intentionally NOT a member-start signal here. A bare `[` after a newline is
    // ambiguous between a computed method name (`[Symbol.iterator]()`) and a computed member
    // access continuation (`foo\n  [bar]`). JavaScript's ASI rule explicitly forbids inserting a
    // `;` before a line that starts with `[`, so any source file that wants the computed-method
    // reading must write an explicit `;` — which the outer loop's `;` branch already handles. That
    // makes "treat `[` as continuation" the safe default for this heuristic.
    // Feed a sanitized (lex-masked) header string; strings/comments must already be blanked.
    // 指定位置がクラスボディの新しいメンバ宣言を始めるかを判定する: `}` (クラス body 閉じ)、
    // `;` (空文)、`#` / `@` / `*<name>` の先頭トークン、あるいは識別子で「クラスメンバ修飾キーワード」
    // または直後が `=` / `(` / `<` / `?` / `!` / `:` / `;` の場合。
    // 注意: `[` はあえて member-start として扱わない。改行直後の素の `[` は computed method name
    // (`[Symbol.iterator]()`) と computed member access の継続 (`foo\n  [bar]`) の両方に見えてしまう。
    // JavaScript の ASI 規則は `[` で始まる行の前に自動で `;` を挿入しないため、計算メンバ名を意図する
    // ソースは明示的に `;` を書く必要があり、そのケースは外側ループの `;` 分岐で既に拾える。よって
    // この ASI ヒューリスティックでは `[` を継続として扱うのが安全な既定。
    // 呼び出し側は lexer でマスク済み (文字列/コメントが blanked) の sanitizedHeader を渡すこと。
    private static bool StartsJavaScriptTypeScriptClassMemberAt(string sanitizedHeader, int index)
    {
        if (index < 0 || index >= sanitizedHeader.Length)
            return false;
        var ch = sanitizedHeader[index];
        if (ch is '}' or ';' or '#' or '@')
            return true;
        if (ch == '*')
        {
            var j = index + 1;
            while (j < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[j]))
                j++;
            if (j >= sanitizedHeader.Length)
                return false;
            var next = sanitizedHeader[j];
            return IsJavaScriptTypeScriptIdentifierStart(next) || next is '#' or '[';
        }
        if (!IsJavaScriptTypeScriptIdentifierStart(ch))
            return false;

        var end = index + 1;
        while (end < sanitizedHeader.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[end]))
            end++;
        var word = sanitizedHeader[index..end];
        if (word is "async" or "static" or "get" or "set" or "public" or "private" or "protected"
            or "readonly" or "override" or "abstract" or "declare" or "accessor" or "constructor")
        {
            return true;
        }

        var after = end;
        while (after < sanitizedHeader.Length && sanitizedHeader[after] != '\n' && char.IsWhiteSpace(sanitizedHeader[after]))
            after++;
        if (after >= sanitizedHeader.Length)
            return false;
        var follow = sanitizedHeader[after];
        return follow is '=' or '(' or '<' or '?' or '!' or ':' or ';';
    }

    // Walks a TypeScript type annotation starting at ':' through to the outer '=' that terminates
    // it (i.e., the class-field assignment operator). `=>` inside the type (arrow types) is
    // treated as a two-char token and skipped; `==` is likewise skipped so we do not terminate on
    // a stray comparison.
    // 型注釈 `:` から、フィールド代入の外側 `=` までを歩く。型内部の `=>` (arrow type) は 2 文字ひと組で
    // 読み飛ばし、`==` も比較演算子として読み飛ばして誤終端しないようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilFieldEquals(string sanitizedHeader, ref int index)
    {
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == '=')
                {
                    if (index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '=')
                    {
                        index += 2;
                        continue;
                    }
                    return true;
                }
                if (ch == ';' || ch == ',')
                    return false;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Walks a TypeScript member-property type annotation from `:` to the terminating `;`.
    // Arrow types inside nested parens / angles / brackets are skipped as two-char tokens so
    // `=>` in function types does not terminate the walk early.
    // TypeScript の member-property 型注釈を `:` から終端 `;` まで歩く。入れ子の
    // 括弧 / 山括弧 / 角括弧内の arrow type は 2 文字トークンとして読み飛ばし、
    // function type 内の `=>` で早期終了しないようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilSemicolon(string sanitizedHeader, ref int index, out int typeEndColumn)
    {
        typeEndColumn = -1;
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                    lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == ';')
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }
                if (!char.IsWhiteSpace(ch))
                    lastNonWs = index;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Walks a TypeScript return-type annotation from ':' to the terminating '=>'. Inner arrow
    // types inside parens/angles/brackets are skipped as two-char tokens without decrementing
    // depth. Returns the inclusive column of the last non-whitespace character of the type.
    // 戻り値型 `:` から最外殻の `=>` までを歩く。括弧/角括弧/山括弧内の arrow type は 2 文字単位で
    // 読み飛ばし深さを下げない。型末尾の非空白位置 (inclusive) を返す。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilArrow(
        string sanitizedHeader,
        ref int index,
        out int typeEndColumn)
    {
        typeEndColumn = -1;
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }
                lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            if (!char.IsWhiteSpace(ch))
                lastNonWs = index;
            index++;
        }

        return false;
    }

    private static bool TryParseJavaScriptTypeScriptMethodHeader(string sanitizedLine, int startColumn, string? lang, out JavaScriptTypeScriptMethodHeaderInfo methodHeader)
    {
        return ParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out methodHeader)
            == JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
    }

    private static bool TryParseJavaScriptTypeScriptMemberPropertyHeader(
        string sanitizedLine,
        int startColumn,
        string? lang,
        bool requireAbstractModifier,
        out string name,
        out string? visibility,
        out int typeStartColumn,
        out int typeEndColumn,
        out int headerEndColumn)
    {
        name = string.Empty;
        visibility = null;
        typeStartColumn = -1;
        typeEndColumn = -1;
        headerEndColumn = -1;

        if (lang != "typescript")
            return false;

        var index = Math.Max(0, startColumn);
        var sawAbstract = false;
        var sawAccessor = false;
        var sawName = false;

        while (index < sanitizedLine.Length)
        {
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (index >= sanitizedLine.Length)
                return false;

            if (!TryReadJavaScriptTypeScriptSourceMethodName(sanitizedLine, ref index, out var token))
                return false;

            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (token is "public" or "private" or "protected")
            {
                visibility = token;
                continue;
            }

            if (token is "static" or "readonly" or "override" or "declare" or "accessor")
            {
                continue;
            }

            if (token == "abstract")
            {
                sawAbstract = true;
                continue;
            }

            if (token == "accessor")
            {
                sawAccessor = true;
                continue;
            }

            if (!IsJavaScriptTypeScriptIdentifierStart(token[0]))
                return false;

            name = token;
            sawName = true;
            break;
        }

        if (!sawName)
            return false;

        if (requireAbstractModifier && !sawAbstract && !sawAccessor)
            return false;

        if (index < sanitizedLine.Length && sanitizedLine[index] == '?')
        {
            index++;
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;
        }

        if (index >= sanitizedLine.Length || sanitizedLine[index] != ':')
            return false;

        typeStartColumn = index;
        if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilSemicolon(sanitizedLine, ref index, out typeEndColumn))
            return false;

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        if (index >= sanitizedLine.Length || sanitizedLine[index] != ';')
            return false;

        headerEndColumn = index;
        return true;
    }

    private static JavaScriptTypeScriptMethodHeaderParseStatus ParseJavaScriptTypeScriptMethodHeader(string sanitizedLine, int startColumn, string? lang, out JavaScriptTypeScriptMethodHeaderInfo methodHeader)
    {
        methodHeader = default;
        var index = Math.Max(0, startColumn);
        string? visibility = null;
        var isAsync = false;

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(sanitizedLine, ref index);

        while (index < sanitizedLine.Length)
        {
            while (true)
            {
                if (!TryReadJavaScriptTypeScriptMethodToken(sanitizedLine, ref index, out var token))
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                    index++;

                if (TypeScriptBareMethodModifiers.Contains(token)
                    && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(sanitizedLine, index))
                {
                    if (token is "public" or "private" or "protected")
                        visibility = token;
                    if (token == "async")
                        isAsync = true;
                    continue;
                }

                var isGenerator = token == "*";
                if (!isGenerator && index < sanitizedLine.Length && sanitizedLine[index] == '*')
                {
                    isGenerator = true;
                    index++;
                    while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                        index++;
                }

                if (isGenerator)
                {
                    if (!TryReadJavaScriptTypeScriptMethodName(sanitizedLine, ref index, out var generatorName))
                        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                    token = generatorName;
                }

                var name = token;

                int? genericStartColumn = null;
                int? genericEndColumn = null;
                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == '<')
                {
                    genericStartColumn = index;
                    var angleDepth = 0;
                    while (index < sanitizedLine.Length)
                    {
                        if (sanitizedLine[index] == '<')
                        {
                            angleDepth++;
                        }
                        else if (sanitizedLine[index] == '=' && index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '>')
                        {
                            index += 2;
                            continue;
                        }
                        else if (sanitizedLine[index] == '>')
                        {
                            angleDepth--;
                            if (angleDepth == 0)
                            {
                                genericEndColumn = index;
                                index++;
                                break;
                            }
                        }

                        index++;
                    }

                    if (genericEndColumn == null)
                        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                    while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                        index++;
                }

                if (index >= sanitizedLine.Length || sanitizedLine[index] != '(')
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                var parenDepth = 0;
                while (index < sanitizedLine.Length)
                {
                    if (sanitizedLine[index] == '(')
                    {
                        parenDepth++;
                    }
                    else if (sanitizedLine[index] == ')')
                    {
                        parenDepth--;
                        if (parenDepth == 0)
                        {
                            index++;
                            break;
                        }
                    }

                    index++;
                }

                if (parenDepth != 0)
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                    index++;

                int? returnTypeStartColumn = null;
                int? returnTypeEndColumn = null;
                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == ':')
                {
                    returnTypeStartColumn = index;
                    index++;
                    var returnParenDepth = 0;
                    var returnBracketDepth = 0;
                    var returnAngleDepth = 0;
                    var returnBraceDepth = 0;
                    var sawReturnTypeToken = false;
                    string? previousReturnToken = ":";

                    while (index < sanitizedLine.Length)
                    {
                        var ch = sanitizedLine[index];
                        if (char.IsWhiteSpace(ch))
                        {
                            index++;
                            continue;
                        }

                        if (ch == ';'
                            && returnParenDepth == 0
                            && returnBracketDepth == 0
                            && returnAngleDepth == 0
                            && returnBraceDepth == 0)
                        {
                            returnTypeEndColumn ??= index - 1;
                            methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, -1, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index, false, isAsync, isGenerator);
                            return JavaScriptTypeScriptMethodHeaderParseStatus.DeclarationOnly;
                        }

                        if (ch == '(')
                        {
                            returnParenDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "(";
                            index++;
                            continue;
                        }

                        if (ch == ')' && returnParenDepth > 0)
                        {
                            returnParenDepth--;
                            previousReturnToken = ")";
                            index++;
                            continue;
                        }

                        if (ch == '[')
                        {
                            returnBracketDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "[";
                            index++;
                            continue;
                        }

                        if (ch == ']' && returnBracketDepth > 0)
                        {
                            returnBracketDepth--;
                            previousReturnToken = "]";
                            index++;
                            continue;
                        }

                        if (ch == '<')
                        {
                            returnAngleDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "<";
                            index++;
                            continue;
                        }

                        if (ch == '>' && returnAngleDepth > 0)
                        {
                            returnAngleDepth--;
                            previousReturnToken = ">";
                            index++;
                            continue;
                        }

                        if (ch == '{')
                        {
                            if (returnParenDepth == 0 && returnBracketDepth == 0 && returnAngleDepth == 0 && returnBraceDepth == 0)
                            {
                                if (CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(previousReturnToken))
                                {
                                    returnBraceDepth++;
                                    sawReturnTypeToken = true;
                                    previousReturnToken = "{";
                                    index++;
                                    continue;
                                }

                                if (sawReturnTypeToken)
                                {
                                    returnTypeEndColumn = index - 1;
                                    methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, index, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index, IsAsync: isAsync, IsGenerator: isGenerator);
                                    return JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
                                }
                            }

                            returnBraceDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "{";
                            index++;
                            continue;
                        }

                        if (ch == '}' && returnBraceDepth > 0)
                        {
                            returnBraceDepth--;
                            previousReturnToken = "}";
                            index++;
                            continue;
                        }

                        if (ch == '?' || ch == ':' || ch == '|' || ch == '&' || ch == ',')
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = ch.ToString();
                            index++;
                            continue;
                        }

                        if (ch == '=' && index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '>')
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = "=>";
                            index += 2;
                            continue;
                        }

                        if (TrySkipTypeScriptTypeToken(sanitizedLine, ref index, out var typeToken))
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = typeToken;
                            continue;
                        }

                        sawReturnTypeToken = true;
                        previousReturnToken = ch.ToString();
                        index++;
                    }

                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;
                }

                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == ';')
                {
                    methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, -1, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index, false);
                    return JavaScriptTypeScriptMethodHeaderParseStatus.DeclarationOnly;
                }

                if (index >= sanitizedLine.Length || sanitizedLine[index] != '{')
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, index, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index, IsAsync: isAsync, IsGenerator: isGenerator);
                return JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
            }
        }

        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;
    }

    private static bool TryCaptureJavaScriptTypeScriptMethodHeader(
        string[] lines,
        int startIndex,
        int startColumn,
        int scanEndExclusive,
        string firstSanitizedLine,
        JavaScriptLexState nextLineLexState,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture methodCapture)
    {
        methodCapture = default;
        var sourceBuilder = new System.Text.StringBuilder();
        var sanitizedBuilder = new System.Text.StringBuilder();

        // Content was split on '\n', so CRLF lines carry a trailing '\r'. Strip it from both
        // builders in lockstep so inter-line separators stay '\n' regardless of source line
        // endings; the sanitized lex output preserves '\r' at the same column as the source,
        // so dropping it from both keeps column mapping aligned (see #382 / #405).
        // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。sanitized 側も
        // source と同じ列に '\r' を保持するため、両方から一律に '\r' を落とせば column
        // mapping はズレず、行間セパレータも OS に依存せず '\n' に揃う（#382 / #405 参照）。
        var firstSourceSegmentRaw = startColumn < lines[startIndex].Length
            ? lines[startIndex][startColumn..]
            : string.Empty;
        var firstSanitizedSegmentRaw = startColumn < firstSanitizedLine.Length
            ? firstSanitizedLine[startColumn..]
            : string.Empty;
        var firstSourceSegment = StripTrailingCr(firstSourceSegmentRaw);
        var firstSanitizedSegment = StripTrailingCr(firstSanitizedSegmentRaw);
        sourceBuilder.Append(firstSourceSegment);
        sanitizedBuilder.Append(lang == "typescript"
            ? NormalizeTypeScriptBareMethodMatchInput(firstSanitizedSegment)
            : firstSanitizedSegment);

        if (TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
            sourceBuilder.ToString(),
            sanitizedBuilder.ToString(),
            startIndex,
            startColumn,
            lang,
            out methodCapture))
        {
            return true;
        }

        var lexState = nextLineLexState;
        for (int lineIndex = startIndex + 1; lineIndex < scanEndExclusive; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;

            var sourceLine = StripTrailingCr(lines[lineIndex]);
            var sanitizedLine = StripTrailingCr(lexedLine.SanitizedLine);
            sourceBuilder.Append('\n');
            sourceBuilder.Append(sourceLine);
            sanitizedBuilder.Append('\n');
            sanitizedBuilder.Append(lang == "typescript"
                ? NormalizeTypeScriptBareMethodMatchInput(sanitizedLine)
                : sanitizedLine);

            if (TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
                sourceBuilder.ToString(),
                sanitizedBuilder.ToString(),
                startIndex,
                startColumn,
                lang,
                out methodCapture))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseJavaScriptTypeScriptAccessorFieldHeader(
        string sanitizedLine,
        int startColumn,
        out string name,
        out string? visibility,
        out int typeStartColumn,
        out int typeEndColumn,
        out int headerEndColumn,
        out bool hasInitializer)
    {
        name = string.Empty;
        visibility = null;
        typeStartColumn = -1;
        typeEndColumn = -1;
        headerEndColumn = -1;
        hasInitializer = false;

        var index = Math.Max(0, startColumn);
        var sawAccessor = false;

        while (index < sanitizedLine.Length)
        {
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (index >= sanitizedLine.Length)
                return false;

            if (!TryReadJavaScriptTypeScriptSourceMethodName(sanitizedLine, ref index, out var token))
                return false;

            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (token is "public" or "private" or "protected")
            {
                visibility = token;
                continue;
            }

            if (token is "static" or "readonly" or "override" or "declare")
                continue;

            if (token == "accessor")
            {
                sawAccessor = true;
                continue;
            }

            if (!IsJavaScriptTypeScriptIdentifierStart(token[0]))
                return false;

            name = token;
            break;
        }

        if (!sawAccessor)
            return false;

        if (index < sanitizedLine.Length && sanitizedLine[index] == '?')
        {
            index++;
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;
        }

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        if (index >= sanitizedLine.Length)
            return false;

        if (sanitizedLine[index] == ':')
        {
            typeStartColumn = index;
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilAccessorTerminator(sanitizedLine, ref index, out typeEndColumn, out hasInitializer))
                return false;

            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;
        }
        else if (sanitizedLine[index] == '=')
        {
            hasInitializer = true;
        }
        else
        {
            return false;
        }

        if (index >= sanitizedLine.Length)
            return false;

        if (sanitizedLine[index] == '=')
        {
            hasInitializer = true;
            headerEndColumn = index;
            return true;
        }

        if (sanitizedLine[index] != ';')
            return false;

        headerEndColumn = index;
        return true;
    }

    // Walks a TypeScript accessor field type annotation from `:` to either a terminating `;` or
    // a field initializer `=`. This mirrors the member-property helper but keeps the auto-accessor
    // initializer boundary visible so the outer scanner can switch into field-initializer mode.
    // TypeScript の accessor field 型注釈を `:` から終端 `;` または initializer `=` まで歩く。
    // member-property 用 helper を踏襲しつつ、auto-accessor の initializer 境界を外側に見せることで、
    // 呼び出し側が field-initializer モードへ切り替えられるようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilAccessorTerminator(
        string sanitizedLine,
        ref int index,
        out int typeEndColumn,
        out bool hasInitializer)
    {
        typeEndColumn = -1;
        hasInitializer = false;

        if (index >= sanitizedLine.Length || sanitizedLine[index] != ':')
            return false;

        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedLine.Length)
        {
            var ch = sanitizedLine[index];

            if (ch == '=' && index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                    lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == ';')
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }

                if (ch == '=')
                {
                    if (index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '=')
                    {
                        index += 2;
                        continue;
                    }

                    typeEndColumn = lastNonWs;
                    hasInitializer = true;
                    return true;
                }

                if (!char.IsWhiteSpace(ch))
                    lastNonWs = index;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Multi-line accumulating wrapper for class-field arrow functions. Mirrors
    // TryCaptureJavaScriptTypeScriptMethodHeader: accumulates sanitized/source lines, calls the
    // arrow-header parser on each accumulation step, and maps the sanitized body-open column back
    // to a source (lineIndex, column) pair. Returns a JavaScriptTypeScriptMethodHeaderCapture so
    // the scanner can emit an arrow field symbol with the same machinery as method headers.
    // クラスフィールドのアロー関数に対する複数行蓄積ラッパー。
    // TryCaptureJavaScriptTypeScriptMethodHeader と同じく sanitized/source を行単位で蓄積し、
    // 蓄積ごとにアローヘッダーパーサを呼び、sanitized 上の body 開始列を source の
    // (行, 列) に逆写像する。戻り値は JavaScriptTypeScriptMethodHeaderCapture を使い回すため、
    // 呼び出し元の emit 処理はメソッドヘッダーと同じフローで扱える。
    private static bool TryCaptureJavaScriptTypeScriptClassFieldArrow(
        string[] lines,
        int startIndex,
        int startColumn,
        int scanEndExclusive,
        string firstSanitizedLine,
        JavaScriptLexState nextLineLexState,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        arrowCapture = default;
        var sourceBuilder = new System.Text.StringBuilder();
        var sanitizedBuilder = new System.Text.StringBuilder();

        var firstSourceSegmentRaw = startColumn < lines[startIndex].Length
            ? lines[startIndex][startColumn..]
            : string.Empty;
        var firstSanitizedSegmentRaw = startColumn < firstSanitizedLine.Length
            ? firstSanitizedLine[startColumn..]
            : string.Empty;
        sourceBuilder.Append(StripTrailingCr(firstSourceSegmentRaw));
        sanitizedBuilder.Append(StripTrailingCr(firstSanitizedSegmentRaw));

        if (TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
            sourceBuilder.ToString(),
            sanitizedBuilder.ToString(),
            startIndex,
            startColumn,
            lang,
            out arrowCapture))
        {
            return true;
        }

        var lexState = nextLineLexState;
        for (int lineIndex = startIndex + 1; lineIndex < scanEndExclusive; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;

            sourceBuilder.Append('\n');
            sourceBuilder.Append(StripTrailingCr(lines[lineIndex]));
            sanitizedBuilder.Append('\n');
            sanitizedBuilder.Append(StripTrailingCr(lexedLine.SanitizedLine));

            if (TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
                sourceBuilder.ToString(),
                sanitizedBuilder.ToString(),
                startIndex,
                startColumn,
                lang,
                out arrowCapture))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
        string sourceHeader,
        string sanitizedHeader,
        int startIndex,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        arrowCapture = default;
        if (!TryParseJavaScriptTypeScriptClassFieldArrowHeader(sanitizedHeader, 0, lang, out var arrowInfo))
            return false;

        if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            arrowInfo.BodyStartColumn,
            out var bodyStartLineIndex,
            out var bodyStartColumn))
        {
            return false;
        }

        int? bodyEndLineIndex = null;
        int? bodyEndColumn = null;
        if (arrowInfo.ExpressionBodyEndColumn is int expressionEnd)
        {
            if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
                sourceHeader,
                startIndex,
                startColumn,
                expressionEnd,
                out var expressionEndLineIndex,
                out var expressionEndColumn))
            {
                return false;
            }
            bodyEndLineIndex = expressionEndLineIndex;
            bodyEndColumn = expressionEndColumn;
        }

        // For brace-body arrow fields, header end == body start (both point at `{`). For
        // expression-body arrow fields, BodyStartColumn points at the first expression char
        // and BodyEndLineIndex/Column describe the last expression char before `;`.
        // block body 矢印 field は header end と body start が同じ `{` を指す。式本体矢印 field は
        // BodyStartColumn が式の先頭、BodyEndLineIndex/Column が `;` 直前の式末尾を指す。
        arrowCapture = new JavaScriptTypeScriptMethodHeaderCapture(
            sourceHeader,
            arrowInfo,
            bodyStartLineIndex,
            bodyStartColumn,
            bodyStartLineIndex,
            bodyStartColumn,
            bodyEndLineIndex,
            bodyEndColumn);
        return true;
    }

    private static bool TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
        string sourceHeader,
        string sanitizedHeader,
        int startIndex,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture methodCapture)
    {
        methodCapture = default;
        var parseStatus = ParseJavaScriptTypeScriptMethodHeader(sanitizedHeader, 0, lang, out var methodHeader);
        if (parseStatus == JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid)
            return false;

        var headerEndLocationColumn = methodHeader.HasBody
            ? methodHeader.BodyStartColumn
            : methodHeader.HeaderEndColumn ?? -1;
        if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            headerEndLocationColumn,
            out var headerEndLineIndex,
            out var headerEndColumn))
        {
            return false;
        }

        var bodyStartLineIndex = -1;
        var bodyStartColumn = -1;
        if (methodHeader.HasBody && !TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            methodHeader.BodyStartColumn,
            out bodyStartLineIndex,
            out bodyStartColumn))
        {
            return false;
        }

        methodCapture = new JavaScriptTypeScriptMethodHeaderCapture(
            sourceHeader,
            methodHeader,
            headerEndLineIndex,
            headerEndColumn,
            bodyStartLineIndex,
            bodyStartColumn);
        return true;
    }

    private static bool TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
        string sourceHeader,
        int startIndex,
        int startColumn,
        int headerColumn,
        out int lineIndex,
        out int column)
    {
        lineIndex = startIndex;
        column = startColumn;
        if (headerColumn < 0 || headerColumn >= sourceHeader.Length)
            return false;

        for (int i = 0; i < headerColumn; i++)
        {
            if (sourceHeader[i] == '\n')
            {
                lineIndex++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return true;
    }

    private static string BuildJavaScriptTypeScriptBareMethodSignature(
        string[] lines,
        int startIndex,
        int startColumn,
        int? bodyEndLine,
        int sameLineMethodEndColumn,
        JavaScriptTypeScriptMethodHeaderCapture methodCapture,
        string? lang)
    {
        if (!methodCapture.HeaderInfo.HasBody)
        {
            if (methodCapture.HeaderEndLineIndex == startIndex && methodCapture.HeaderEndColumn >= startColumn)
                return lines[startIndex].AsSpan(startColumn, methodCapture.HeaderEndColumn + 1 - startColumn).Trim().ToString();

            if (methodCapture.HeaderInfo.HeaderEndColumn != null
                && methodCapture.HeaderInfo.HeaderEndColumn.Value >= 0
                && methodCapture.HeaderInfo.HeaderEndColumn.Value < methodCapture.SourceHeader.Length)
            {
                return methodCapture.SourceHeader.AsSpan(0, methodCapture.HeaderInfo.HeaderEndColumn.Value + 1).Trim().ToString();
            }

            return methodCapture.SourceHeader.Trim();
        }

        if (bodyEndLine == startIndex + 1 && sameLineMethodEndColumn >= startColumn)
            return lines[startIndex].AsSpan(startColumn, sameLineMethodEndColumn + 1 - startColumn).Trim().ToString();

        if (methodCapture.HeaderInfo.BodyStartColumn < 0
            || methodCapture.HeaderInfo.BodyStartColumn >= methodCapture.SourceHeader.Length)
        {
            return methodCapture.SourceHeader.Trim();
        }

        return methodCapture.SourceHeader.AsSpan(0, methodCapture.HeaderInfo.BodyStartColumn + 1).Trim().ToString();
    }

    // Build a signature string for a class-field arrow function. Same shape as the method-header
    // signature builder (same-line bodies quote the source slice verbatim, multi-line bodies stop
    // at the '{' that opens the block body).
    // クラスフィールドのアロー関数向けのシグネチャ文字列を組み立てる。メソッドヘッダー版と同じ方針で、
    // 同一行 body は source をそのまま切り出し、複数行 body はブロック本体を開く '{' まで切り出す。
    private static string BuildJavaScriptTypeScriptClassFieldArrowSignature(
        string[] lines,
        int startIndex,
        int startColumn,
        int? bodyEndLine,
        int sameLineArrowEndColumn,
        JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        if (bodyEndLine == startIndex + 1 && sameLineArrowEndColumn >= startColumn)
            return lines[startIndex].AsSpan(startColumn, sameLineArrowEndColumn + 1 - startColumn).Trim().ToString();

        // For expression-body arrow fields that span multiple lines, include the full source up
        // to and including the last expression char (before `;`) so the signature reflects the
        // whole `name = (args) => expr` shape.
        // 複数行にわたる式本体矢印 field では、`;` 直前の式末尾までをシグネチャに含めて
        // `name = (args) => expr` 全体が見えるようにする。
        if (arrowCapture.HeaderInfo.ExpressionBodyEndColumn is int expressionEnd
            && expressionEnd >= 0
            && expressionEnd + 1 <= arrowCapture.SourceHeader.Length)
        {
            return arrowCapture.SourceHeader.AsSpan(0, expressionEnd + 1).Trim().ToString();
        }

        if (arrowCapture.HeaderInfo.BodyStartColumn < 0
            || arrowCapture.HeaderInfo.BodyStartColumn >= arrowCapture.SourceHeader.Length)
        {
            return arrowCapture.SourceHeader.Trim();
        }

        return arrowCapture.SourceHeader.AsSpan(0, arrowCapture.HeaderInfo.BodyStartColumn + 1).Trim().ToString();
    }

    private static string? GetJavaScriptTypeScriptBareMethodReturnType(string sourceHeader, JavaScriptTypeScriptMethodHeaderInfo methodHeader, string? lang)
    {
        if (lang != "typescript"
            || methodHeader.ReturnTypeStartColumn == null
            || methodHeader.ReturnTypeEndColumn == null)
            return null;

        var returnTypeStartColumn = methodHeader.ReturnTypeStartColumn.Value + 1;
        var returnTypeEndColumn = methodHeader.ReturnTypeEndColumn.Value;
        if (returnTypeEndColumn < returnTypeStartColumn || returnTypeEndColumn >= sourceHeader.Length)
            return null;

        return NormalizeMetadata(sourceHeader[returnTypeStartColumn..(returnTypeEndColumn + 1)]);
    }

    private static string ResolveJavaScriptTypeScriptFunctionKindFromHeader(JavaScriptTypeScriptMethodHeaderInfo methodHeader)
        => ResolveJavaScriptTypeScriptFunctionKind(methodHeader.IsAsync, methodHeader.IsGenerator);

    private static string ResolveJavaScriptTypeScriptFunctionKind(bool isAsync, bool isGenerator)
        => (isAsync, isGenerator) switch
        {
            (true, true) => "async_generator",
            (true, false) => "async_function",
            (false, true) => "generator",
            _ => "function",
        };

    private static bool TryGetJavaScriptTypeScriptNextToken(
        string[] lines,
        int startIndex,
        int startColumn,
        bool skipWrappingParens,
        out int tokenLineIndex,
        out int tokenStartColumn,
        out string? token)
    {
        tokenLineIndex = -1;
        tokenStartColumn = -1;
        token = null;

        var lexState = new JavaScriptLexState();
        for (int lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == startIndex ? startColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (skipWrappingParens && ch == '(')
                {
                    column++;
                    continue;
                }

                if (!IsJavaScriptTypeScriptIdentifierStart(ch))
                    return false;

                var tokenStart = column;
                column++;
                while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                    column++;

                tokenLineIndex = lineIndex;
                tokenStartColumn = tokenStart;
                token = sanitizedLine[tokenStart..column];
                return true;
            }
        }

        return false;
    }

    private static bool IsJavaScriptTypeScriptIdentifierStart(char ch) =>
        char.IsLetter(ch) || ch == '_' || ch == '$';

    private static bool IsJavaScriptTypeScriptIdentifierPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_' || ch == '$';

    private static bool TrySkipTypeScriptTypeToken(string sanitizedLine, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        return TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref index, out token)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref index, out token)
            || TryReadJavaScriptTypeScriptIdentifierToken(sanitizedLine, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptIdentifierToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || !IsJavaScriptTypeScriptIdentifierStart(input[index]))
            return false;

        var tokenStart = index;
        index++;
        while (index < input.Length && IsJavaScriptTypeScriptIdentifierPart(input[index]))
            index++;

        token = input[tokenStart..index];
        return true;
    }

    private static bool TryReadJavaScriptTypeScriptQuotedLiteralToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || input[index] is not ('\'' or '"' or '`'))
            return false;

        var probe = index;
        var delimiter = input[probe];
        var tokenStart = probe;
        var escapeNext = false;
        probe++;
        while (probe < input.Length)
        {
            var ch = input[probe];
            if (escapeNext)
            {
                escapeNext = false;
                probe++;
                continue;
            }

            if (ch == '\\')
            {
                escapeNext = true;
                probe++;
                continue;
            }

            if (ch == delimiter)
            {
                probe++;
                index = probe;
                token = input[tokenStart..index];
                return true;
            }

            probe++;
        }

        return false;
    }

    private static bool TryReadJavaScriptTypeScriptNumericLiteralToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || !char.IsDigit(input[index]))
            return false;

        var tokenStart = index;
        if (input[index] == '0' && index + 1 < input.Length && input[index + 1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            index += 2;
            while (index < input.Length && IsJavaScriptTypeScriptNumericLiteralPart(input[index], allowDecimalPoint: false))
                index++;
        }
        else
        {
            while (index < input.Length && IsJavaScriptTypeScriptNumericLiteralPart(input[index], allowDecimalPoint: true))
                index++;
        }

        token = input[tokenStart..index];
        return true;
    }

    private static bool IsJavaScriptTypeScriptNumericLiteralPart(char ch, bool allowDecimalPoint)
    {
        if (char.IsLetterOrDigit(ch) || ch == '_')
            return true;

        return allowDecimalPoint && ch == '.';
    }

    private static bool TryReadJavaScriptTypeScriptMethodToken(string sanitizedLine, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        if (sanitizedLine[index] == '*')
        {
            token = "*";
            index++;
            return true;
        }

        return TryReadJavaScriptTypeScriptMethodName(sanitizedLine, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptMethodName(string sanitizedLine, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        var tokenStart = index;
        if (TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref index, out name)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref index, out name))
        {
            return true;
        }

        if (sanitizedLine[index] == '[')
        {
            var bracketDepth = 0;
            while (index < sanitizedLine.Length)
            {
                if (sanitizedLine[index] == '[')
                    bracketDepth++;
                else if (sanitizedLine[index] == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        index++;
                        name = sanitizedLine[tokenStart..index];
                        return true;
                    }
                }

                index++;
            }

            return false;
        }

        if (sanitizedLine[index] == '#')
        {
            index++;
            if (index >= sanitizedLine.Length || !IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
                return false;
        }
        else if (!IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
        {
            return false;
        }

        index++;
        while (index < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index]))
            index++;

        name = sanitizedLine[tokenStart..index];
        return true;
    }

    private static bool TrySkipJavaScriptTypeScriptDecorators(string line, ref int index)
    {
        var skippedAny = false;

        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index >= line.Length || line[index] != '@')
                return skippedAny;

            skippedAny = true;
            index++;

            var parenDepth = 0;
            var bracketDepth = 0;
            var braceDepth = 0;

            while (index < line.Length)
            {
                var ch = line[index];
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && char.IsWhiteSpace(ch))
                    break;

                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == '{')
                    braceDepth++;
                else if (ch == '}' && braceDepth > 0)
                    braceDepth--;

                index++;
            }
        }

        return skippedAny;
    }

    private static string? GetJavaScriptTypeScriptMethodNameFromSource(string line, int startColumn)
    {
        var index = Math.Max(0, startColumn);
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(line, ref index);

        while (index < line.Length)
        {
            if (!TryReadJavaScriptTypeScriptSourceMethodToken(line, ref index, out var token))
                return null;

            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (TypeScriptBareMethodModifiers.Contains(token)
                && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(line, index))
            {
                continue;
            }

            var isGenerator = token == "*";
            if (!isGenerator && index < line.Length && line[index] == '*')
            {
                isGenerator = true;
                index++;
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                    index++;
            }

            if (isGenerator)
                return TryReadJavaScriptTypeScriptSourceMethodName(line, ref index, out var generatorName) ? generatorName : null;

            return token;
        }

        return null;
    }

    private static bool TryReadJavaScriptTypeScriptSourceMethodToken(string line, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= line.Length)
            return false;

        if (line[index] == '*')
        {
            token = "*";
            index++;
            return true;
        }

        return TryReadJavaScriptTypeScriptSourceMethodName(line, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptSourceQuotedLiteralToken(string line, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= line.Length || line[index] is not ('\'' or '"' or '`'))
            return false;

        var delimiter = line[index];
        var tokenStart = index;
        var escapeNext = false;
        index++;
        while (index < line.Length)
        {
            var ch = line[index];
            if (escapeNext)
            {
                escapeNext = false;
                index++;
                continue;
            }

            if (ch == '\\')
            {
                escapeNext = true;
                index++;
                continue;
            }

            if (ch == delimiter)
            {
                index++;
                token = line[tokenStart..index];
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool TryReadJavaScriptTypeScriptSourceMethodName(string line, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= line.Length)
            return false;

        var tokenStart = index;
        if (TryReadJavaScriptTypeScriptSourceQuotedLiteralToken(line, ref index, out name)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(line, ref index, out name))
        {
            return true;
        }

        if (line[index] == '[')
        {
            var bracketDepth = 0;
            var inSingleQuote = false;
            var inDoubleQuote = false;
            var inTemplateString = false;
            var escapeNext = false;
            while (index < line.Length)
            {
                var ch = line[index];
                if (escapeNext)
                {
                    escapeNext = false;
                    index++;
                    continue;
                }

                if (inSingleQuote)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '\'')
                        inSingleQuote = false;
                    index++;
                    continue;
                }

                if (inDoubleQuote)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '"')
                        inDoubleQuote = false;
                    index++;
                    continue;
                }

                if (inTemplateString)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '`')
                        inTemplateString = false;
                    index++;
                    continue;
                }

                if (ch == '\'')
                {
                    inSingleQuote = true;
                    index++;
                    continue;
                }

                if (ch == '"')
                {
                    inDoubleQuote = true;
                    index++;
                    continue;
                }

                if (ch == '`')
                {
                    inTemplateString = true;
                    index++;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        index++;
                        name = line[tokenStart..index];
                        return true;
                    }
                }

                index++;
            }

            return false;
        }

        if (line[index] == '#')
        {
            index++;
            if (index >= line.Length || !IsJavaScriptTypeScriptIdentifierStart(line[index]))
                return false;
        }
        else if (!IsJavaScriptTypeScriptIdentifierStart(line[index]))
        {
            return false;
        }

        index++;
        while (index < line.Length && IsJavaScriptTypeScriptIdentifierPart(line[index]))
            index++;

        name = line[tokenStart..index];
        return true;
    }

    private static int FindNextJavaScriptTypeScriptTokenStart(string sanitizedLine, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < sanitizedLine.Length)
        {
            if (!IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
            {
                index++;
                continue;
            }

            if (index > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index - 1]))
            {
                index++;
                continue;
            }

            return index;
        }

        return -1;
    }

    private static int FindNextJavaScriptTypeScriptStatementStart(string sanitizedLine, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < sanitizedLine.Length)
        {
            index = FindNextJavaScriptTypeScriptTokenStart(sanitizedLine, index);
            if (index < 0)
                return -1;

            var previous = index - 1;
            while (previous >= 0 && char.IsWhiteSpace(sanitizedLine[previous]))
                previous--;

            if (previous < 0 || sanitizedLine[previous] is ';' or '{' or '}')
                return index;

            index++;
        }

        return -1;
    }

    private static bool CanTreatJavaScriptTypeScriptMethodTokenAsModifier(string sanitizedLine, int index)
    {
        var lookahead = index;
        while (lookahead < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[lookahead]))
            lookahead++;

        if (lookahead >= sanitizedLine.Length)
            return false;

        var ch = sanitizedLine[lookahead];
        if (ch is '(' or '<')
            return false;

        if (ch == '*')
        {
            lookahead++;
            while (lookahead < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[lookahead]))
                lookahead++;

            if (lookahead >= sanitizedLine.Length)
                return false;

            return sanitizedLine[lookahead] is '[' or '#'
                || IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[lookahead]);
        }

        return ch is '[' or '#'
            || IsJavaScriptTypeScriptIdentifierStart(ch);
    }

    private static bool CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(string? previousReturnToken)
    {
        return previousReturnToken is ":" or "?" or "|" or "&" or "," or "(" or "[" or "=>" or "extends";
    }

}
