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
