using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptCommonJsNamedExportAssignments(
        long fileId,
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        for (int i = 0; i < sanitizedLines.Length; i++)
        {
            var sanitizedLine = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (statementStart >= 0)
            {
                var statementSlice = sanitizedLine[statementStart..];
                var match = JavaScriptTypeScriptCommonJsNamedExportAssignmentRegex.Match(statementSlice);
                if (!match.Success)
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var absoluteMatchIndex = statementStart + match.Index;
                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, absoluteMatchIndex, sanitizedLine, includeBlockScope: false)
                    || IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, absoluteMatchIndex, sanitizedLine))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var name = TryGetGroup(match, "name")
                    ?? TryGetGroup(match, "numericBracketName")
                    ?? GetJavaScriptTypeScriptCommonJsBracketName(rawLines[i], absoluteMatchIndex + match.Groups["bracketName"].Index, match.Groups["bracketName"].Length);
                if (name == null)
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }
                if (!TryCollectJavaScriptTypeScriptAssignedRhs(
                        rawLines,
                        sanitizedLines,
                        i,
                        absoluteMatchIndex,
                        statementStart + match.Groups["rhs"].Index,
                        lang,
                        out var rhs,
                        out var rhsStartLineIndex,
                        out var rhsStartColumn,
                        out var rhsEndLineIndex,
                        out var rhsEndColumn,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var classificationRhs = StartsJavaScriptTypeScriptPotentialGenericArrowAssignmentValue(rhs)
                    ? CollectJavaScriptTypeScriptAssignedRhsHeader(sanitizedLines, rhsStartLineIndex, rhsStartColumn)
                    : rhs;

                if (!StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    && TryFindJavaScriptTypeScriptAssignedRhsStart(
                             sanitizedLines,
                             i,
                             statementStart + match.Groups["rhs"].Index,
                             out var fallbackRhsStartLineIndex,
                             out var fallbackRhsStartColumn))
                {
                    var fallbackClassificationRhs = CollectJavaScriptTypeScriptAssignedRhsHeader(
                        sanitizedLines,
                        fallbackRhsStartLineIndex,
                        fallbackRhsStartColumn);
                    if (StartsJavaScriptTypeScriptFunctionAssignmentValue(fallbackClassificationRhs))
                        classificationRhs = fallbackClassificationRhs;
                }

                if (classificationRhs.Length == 0
                    || StartsJavaScriptTypeScriptClassAssignmentValue(classificationRhs))
                {
                    if (rhsEndLineIndex > i)
                        i = rhsEndLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], rhsEndColumn + 1);
                    continue;
                }

                var kind = StartsJavaScriptTypeScriptLambdaAssignmentValue(classificationRhs)
                    ? "lambda"
                    : StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    ? "function"
                    : "property";

                int? bodyStartLine = null;
                int? bodyEndLine = null;
                if (kind is "function" or "lambda")
                {
                    if (TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
                            rawLines,
                            rhsStartLineIndex,
                            rhsStartColumn,
                            lang,
                            out var openBraceLineIndex,
                            out var openBraceColumn))
                    {
                        var (_, resolvedBodyStartLine, resolvedBodyEndLine) = ResolveRange(rawLines, openBraceLineIndex, BodyStyle.Brace, lang, openBraceColumn);
                        bodyStartLine = resolvedBodyStartLine;
                        bodyEndLine = resolvedBodyEndLine;
                    }
                }

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    i + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = kind,
                        Name = name,
                        Line = i + 1,
                        StartLine = i + 1,
                        StartColumn = absoluteMatchIndex,
                        EndLine = Math.Max(i + 1, bodyEndLine ?? (i + 1)),
                        BodyStartLine = bodyStartLine,
                        BodyEndLine = bodyEndLine,
                        Signature = signature,
                        Visibility = "export",
                    },
                    rawLines[i]);

                if (rhsEndLineIndex > i)
                    i = rhsEndLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], rhsEndColumn + 1);
            }
        }
    }

    private static string? GetJavaScriptTypeScriptCommonJsBracketName(string rawLine, int startColumn, int length)
    {
        if (length <= 0 || startColumn < 0 || startColumn + length > rawLine.Length)
            return null;

        var rawName = rawLine.AsSpan(startColumn, length).Trim();
        return rawName.IsEmpty ? null : rawName.ToString();
    }

    private static void ExtractJavaScriptTypeScriptCommonJsDefinePropertyExports(
        long fileId,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        for (int i = 0; i < sanitizedLines.Length; i++)
        {
            var sanitizedLine = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (statementStart >= 0)
            {
                if (!TryReadJavaScriptTypeScriptCommonJsDefinePropertyExport(
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        out var propertyName,
                        out var propertyLineIndex,
                        out var propertyStartColumn,
                        out var endLineIndex,
                        out var endColumn,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, statementStart, sanitizedLine, includeBlockScope: false)
                    || IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, statementStart, sanitizedLine))
                {
                    if (endLineIndex > i)
                    {
                        i = endLineIndex;
                        sanitizedLine = sanitizedLines[i];
                    }

                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, endColumn + 1);
                    continue;
                }

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    propertyLineIndex + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = propertyName,
                        Line = propertyLineIndex + 1,
                        StartLine = propertyLineIndex + 1,
                        StartColumn = propertyStartColumn,
                        EndLine = endLineIndex + 1,
                        Signature = signature,
                        Visibility = "export",
                    },
                    rawLines[propertyLineIndex]);

                if (endLineIndex > i)
                {
                    i = endLineIndex;
                    sanitizedLine = sanitizedLines[i];
                }

                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, endColumn + 1);
            }
        }
    }

    private static bool TryReadJavaScriptTypeScriptCommonJsDefinePropertyExport(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out string propertyName,
        out int propertyLineIndex,
        out int propertyStartColumn,
        out int endLineIndex,
        out int endColumn,
        out string signature)
    {
        propertyName = string.Empty;
        propertyLineIndex = -1;
        propertyStartColumn = -1;
        endLineIndex = -1;
        endColumn = -1;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        var objectColumn = SkipWhitespace(startLine, startColumn);
        const string definePropertyCall = "Object.defineProperty";
        if (!startLine.AsSpan(objectColumn).StartsWith(definePropertyCall, StringComparison.Ordinal))
            return false;

        var afterCallColumn = objectColumn + definePropertyCall.Length;
        if (afterCallColumn < startLine.Length && IsJavaScriptTypeScriptIdentifierPart(startLine[afterCallColumn]))
            return false;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 32);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterCallColumn,
                scanEndExclusive,
                out var openParenLineIndex,
                out var openParenColumn)
            || sanitizedLines[openParenLineIndex][openParenColumn] != '(')
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                openParenLineIndex,
                openParenColumn + 1,
                scanEndExclusive,
                out var targetLineIndex,
                out var targetColumn)
            || !TryReadJavaScriptTypeScriptCommonJsDefinePropertyTarget(
                sanitizedLines[targetLineIndex],
                targetColumn,
                out var targetEndColumn))
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                targetLineIndex,
                targetEndColumn,
                scanEndExclusive,
                out var commaLineIndex,
                out var commaColumn)
            || sanitizedLines[commaLineIndex][commaColumn] != ',')
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                commaLineIndex,
                commaColumn + 1,
                scanEndExclusive,
                out propertyLineIndex,
                out var propertyQuoteColumn))
        {
            return false;
        }

        int propertyEndColumn;
        var sanitizedQuote = sanitizedLines[propertyLineIndex][propertyQuoteColumn];
        if (sanitizedQuote is '\'' or '"')
        {
            if (!TryReadJavaScriptTypeScriptQuotedExportPropertyName(
                    rawLines[propertyLineIndex],
                    sanitizedLines[propertyLineIndex],
                    propertyQuoteColumn,
                    out propertyName,
                    out propertyStartColumn,
                    out var quotedPropertyEndColumn))
            {
                return false;
            }

            propertyEndColumn = quotedPropertyEndColumn;
        }
        else if (char.IsDigit(sanitizedQuote))
        {
            if (!TryReadJavaScriptTypeScriptNumericExportPropertyName(
                    rawLines[propertyLineIndex],
                    sanitizedLines[propertyLineIndex],
                    propertyQuoteColumn,
                    out propertyName,
                    out propertyStartColumn,
                    out var numericPropertyEndColumn))
            {
                return false;
            }

            propertyEndColumn = numericPropertyEndColumn;
        }
        else
        {
            return false;
        }

        if (propertyName == "__esModule")
            return false;

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                propertyLineIndex,
                propertyEndColumn + 1,
                scanEndExclusive,
                out var afterPropertyLineIndex,
                out var afterPropertyColumn)
            || sanitizedLines[afterPropertyLineIndex][afterPropertyColumn] != ',')
        {
            return false;
        }

        if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                sanitizedLines,
                openParenLineIndex,
                openParenColumn,
                scanEndExclusive,
                out endLineIndex,
                out endColumn))
        {
            return false;
        }

        signature = BuildJavaScriptTypeScriptStatementSignature(rawLines, startLineIndex, objectColumn, endLineIndex, endColumn);
        return propertyName.Length > 0;
    }

    private static bool TryReadJavaScriptTypeScriptCommonJsDefinePropertyTarget(
        string sanitizedLine,
        int targetColumn,
        out int targetEndColumn)
    {
        targetEndColumn = targetColumn;

        if (IsJavaScriptTypeScriptKeywordAt(sanitizedLine, targetColumn, "exports"))
        {
            targetEndColumn = targetColumn + "exports".Length;
            return true;
        }

        const string moduleExportsTarget = "module.exports";
        if (!sanitizedLine.AsSpan(targetColumn).StartsWith(moduleExportsTarget, StringComparison.Ordinal))
            return false;

        var endColumn = targetColumn + moduleExportsTarget.Length;
        if (endColumn < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[endColumn]))
            return false;

        targetEndColumn = endColumn;
        return true;
    }

    private static bool TryReadJavaScriptTypeScriptQuotedExportPropertyName(
        string rawLine,
        string sanitizedLine,
        int quoteColumn,
        out string propertyName,
        out int propertyStartColumn,
        out int propertyEndColumn)
    {
        propertyName = string.Empty;
        propertyStartColumn = -1;
        propertyEndColumn = -1;

        if (quoteColumn < 0
            || quoteColumn >= sanitizedLine.Length
            || quoteColumn >= rawLine.Length
            || sanitizedLine[quoteColumn] is not ('\'' or '"'))
        {
            return false;
        }

        var probe = quoteColumn;
        if (!TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref probe, out _))
            return false;

        var rawEndColumn = Math.Min(probe, rawLine.Length);
        var rawName = rawLine[quoteColumn..rawEndColumn].Trim();
        if (rawName.Length < 2
            || rawName[0] != rawName[^1]
            || rawName[0] is not ('\'' or '"'))
        {
            return false;
        }

        propertyName = NormalizeJavaScriptTypeScriptExportedSpecifierName(rawName);
        propertyStartColumn = quoteColumn + 1;
        propertyEndColumn = probe - 1;
        return propertyName.Length > 0;
    }

    private static bool TryReadJavaScriptTypeScriptNumericExportPropertyName(
        string rawLine,
        string sanitizedLine,
        int numberColumn,
        out string propertyName,
        out int propertyStartColumn,
        out int propertyEndColumn)
    {
        propertyName = string.Empty;
        propertyStartColumn = -1;
        propertyEndColumn = -1;

        if (numberColumn < 0
            || numberColumn >= sanitizedLine.Length
            || numberColumn >= rawLine.Length
            || !char.IsDigit(sanitizedLine[numberColumn]))
        {
            return false;
        }

        var probe = numberColumn;
        if (!TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
            return false;

        var rawEndColumn = Math.Min(probe, rawLine.Length);
        propertyName = rawLine[numberColumn..rawEndColumn].Trim();
        propertyStartColumn = numberColumn;
        propertyEndColumn = probe - 1;
        return propertyName.Length > 0;
    }
}
