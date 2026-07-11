using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptQualifiedAssignments(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        Func<JavaScriptScopePrivacyFlags[][]> getPrivateScopeColumns,
        Func<string[]> getSanitizedLines)
    {
        if (!LinesContain(lines, '='))
            return;

        var privateScopeColumns = getPrivateScopeColumns();
        var sanitizedLines = getSanitizedLines();
        List<JavaScriptClassScanTarget>? syntheticClassTargets = null;
        HashSet<SymbolLineIdentity>? symbolLineIdentities = null;
        HashSet<(int StartIndex, int StartColumn, int ScanStartIndex, int ScanEndExclusive, int FirstLineScanOffset, string ContainerKind, string ContainerName)>? targetIdentities = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var sanitizedLine = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (statementStart >= 0)
            {
                var statementSlice = sanitizedLine[statementStart..];
                var match = JavaScriptTypeScriptQualifiedAssignmentRegex.Match(statementSlice);
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

                var name = match.Groups["name"].Value;
                if (!TryCollectJavaScriptTypeScriptAssignedRhs(
                        lines,
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

                if (classificationRhs.Length == 0)
                {
                    if (rhsEndLineIndex > i)
                        i = rhsEndLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, rhsEndColumn + 1);
                    continue;
                }

                if (StartsJavaScriptTypeScriptClassAssignmentValue(classificationRhs))
                {
                    if (!TryGetJavaScriptTypeScriptNextToken(
                        lines,
                        rhsStartLineIndex,
                        rhsStartColumn,
                        skipWrappingParens: true,
                        out var classTokenLineIndex,
                        out var classTokenStartColumn,
                        out _))
                    {
                        statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                        continue;
                    }

                    AddJavaScriptTypeScriptSyntheticClassTarget(
                        fileId,
                        lang,
                        lines,
                        symbols,
                        syntheticClassTargets ??= [],
                        symbolLineIdentities ??= BuildSymbolLineIdentities(symbols, lines.Length),
                        targetIdentities ??= [],
                        i,
                        absoluteMatchIndex,
                        classTokenLineIndex,
                        classTokenStartColumn,
                        name,
                        visibility: null);

                    if (rhsEndLineIndex > i)
                        i = rhsEndLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, rhsEndColumn + 1);
                    continue;
                }

                var kind = StartsJavaScriptTypeScriptLambdaAssignmentValue(classificationRhs)
                    ? "lambda"
                    : StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    ? "function"
                    : "property";

                int? bodyStartLine = null;
                int? bodyEndLine = null;
                if (kind is "function" or "lambda"
                    && TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
                        lines,
                        rhsStartLineIndex,
                        rhsStartColumn,
                        lang,
                        out var openBraceLineIndex,
                        out var openBraceColumn))
                {
                    var (_, resolvedBodyStartLine, resolvedBodyEndLine) = ResolveRange(lines, openBraceLineIndex, BodyStyle.Brace, lang, openBraceColumn);
                    bodyStartLine = resolvedBodyStartLine;
                    bodyEndLine = resolvedBodyEndLine;
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
                    },
                    lines[i]);

                if (rhsEndLineIndex > i)
                    i = rhsEndLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, rhsEndColumn + 1);
            }
        }

        if (syntheticClassTargets is { Count: > 0 })
            ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, syntheticClassTargets);
    }
}
