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
}
