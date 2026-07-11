using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptReExportSymbols(long fileId, string lang, string[] rawLines, string[] sanitizedLines, List<SymbolRecord> symbols)
    {
        for (int i = 0; i < sanitizedLines.Length; i++)
        {
            var line = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(line, 0);
            while (statementStart >= 0)
            {
                if (TryCollectJavaScriptTypeScriptStarReExportClause(
                        lang,
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        out var starEndLineIndex,
                        out var starEndColumn,
                        out var starClause,
                        out var starSignature,
                        out var starStartColumn))
                {
                    var starMatch = JavaScriptTypeScriptStarReExportRegex.Match(starClause);
                    if (starMatch.Success)
                    {
                        if (TryExtractJavaScriptTypeScriptReExportModuleName(
                                rawLines,
                                sanitizedLines,
                                i,
                                starEndLineIndex,
                                starStartColumn,
                                waitForClosedSpecifierList: false,
                                out var moduleName))
                        {
                            AddSymbolRecord(
                                symbols,
                                cssSeenSymbols: null,
                                i + 1,
                                new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "import",
                                    Name = moduleName,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    StartColumn = starStartColumn,
                                    EndLine = starEndLineIndex + 1,
                                    Signature = starSignature,
                                    Visibility = "export",
                                },
                                rawLines[i]);
                        }

                        var namespaceName = starMatch.Groups["namespace"].Value;
                        if (namespaceName.Length > 0)
                        {
                            AddSymbolRecord(
                                symbols,
                                cssSeenSymbols: null,
                                i + 1,
                                new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "property",
                                    Name = namespaceName,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    StartColumn = starStartColumn,
                                    EndLine = starEndLineIndex + 1,
                                    Signature = starSignature,
                                    Visibility = "export",
                                },
                                rawLines[i]);
                        }
                    }

                    if (starEndLineIndex > i)
                    {
                        i = starEndLineIndex;
                        statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], starEndColumn + 1);
                    }
                    else
                    {
                        statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], starEndColumn + 1);
                    }

                    continue;
                }

                if (!TryCollectJavaScriptTypeScriptNamedReExportClause(
                        lang,
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        out var endLineIndex,
                        out var endColumn,
                        out var clause,
                        out var signatureText,
                        out var startColumnText))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], statementStart + 1);
                    continue;
                }

                var namedMatch = JavaScriptTypeScriptNamedReExportRegex.Match(clause);
                if (!namedMatch.Success)
                {
                    if (endLineIndex > i)
                        i = endLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], endColumn + 1);
                    continue;
                }

                if (TryExtractJavaScriptTypeScriptReExportModuleName(
                        rawLines,
                        sanitizedLines,
                        i,
                        endLineIndex,
                        startColumnText,
                        waitForClosedSpecifierList: true,
                        out var namedModuleName))
                {
                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "import",
                            Name = namedModuleName,
                            Line = i + 1,
                            StartLine = i + 1,
                            StartColumn = startColumnText,
                            EndLine = endLineIndex + 1,
                            Signature = signatureText,
                            Visibility = "export",
                        },
                        rawLines[i]);
                }

                var sanitizedReExportSpecifiers = namedMatch.Groups["specifiers"].Value;
                var reExportSpecifiers = ContainsJavaScriptTypeScriptStringLiteralSpecifierName(sanitizedReExportSpecifiers)
                    && TryExtractJavaScriptTypeScriptExportSpecifierListFromSignature(signatureText, out var rawReExportSpecifiers)
                    ? StripJavaScriptTypeScriptSpecifierComments(rawReExportSpecifiers)
                    : sanitizedReExportSpecifiers;
                foreach (var exportedName in ParseJavaScriptTypeScriptReExportedNames(reExportSpecifiers))
                {
                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = exportedName,
                            Line = i + 1,
                            StartLine = i + 1,
                            StartColumn = startColumnText,
                            EndLine = endLineIndex + 1,
                            Signature = signatureText,
                            Visibility = "export",
                        },
                        rawLines[i]);
                }

                if (endLineIndex > i)
                    i = endLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], endColumn + 1);
            }
        }
    }

    private static bool TryCollectJavaScriptTypeScriptStarReExportClause(
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out int endLineIndex,
        out int endColumn,
        out string clause,
        out string signature,
        out int startColumnText)
    {
        endLineIndex = startLineIndex;
        endColumn = -1;
        clause = string.Empty;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        if (startColumn < 0 || startColumn >= startLine.Length)
        {
            startColumnText = -1;
            return false;
        }

        var startLineSlice = startLine.AsSpan(startColumn);
        var trimmedStartLine = startLineSlice.TrimStart();
        if (trimmedStartLine.IsEmpty
            || !trimmedStartLine.StartsWith("export", StringComparison.Ordinal))
        {
            startColumnText = -1;
            return false;
        }

        var exportRemainder = trimmedStartLine["export".Length..].TrimStart();
        var starRemainder = SkipJavaScriptTypeScriptTypeOnlyExportModifier(exportRemainder);
        if (starRemainder.Length > 0 && starRemainder[0] != '*')
        {
            startColumnText = -1;
            return false;
        }

        startColumnText = startColumn + startLineSlice.IndexOf("export", StringComparison.Ordinal);

        var builderCapacity = EstimateJavaScriptTypeScriptStatementCapacity(sanitizedLines, startLineIndex);
        var clauseBuilder = new System.Text.StringBuilder(builderCapacity);
        var signatureBuilder = new System.Text.StringBuilder(builderCapacity);

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var rawLine = rawLines[lineIndex];
            var lineStartColumn = lineIndex == startLineIndex ? startColumnText : 0;
            var lineEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, lineStartColumn, lang);
            var lineEndExclusive = lineEndColumn >= lineStartColumn
                ? lineEndColumn + 1
                : sanitizedLine.Length;

            var sanitizedSlice = sanitizedLine.AsSpan(lineStartColumn, lineEndExclusive - lineStartColumn).Trim();
            if (sanitizedSlice.Length > 0)
            {
                if (clauseBuilder.Length > 0)
                    clauseBuilder.Append(' ');
                clauseBuilder.Append(sanitizedSlice);
            }

            var rawSliceEndExclusive = Math.Min(rawLine.Length, lineEndExclusive);
            var rawSlice = rawLine.AsSpan(lineStartColumn, rawSliceEndExclusive - lineStartColumn).Trim();
            if (rawSlice.Length > 0)
            {
                if (signatureBuilder.Length > 0)
                    signatureBuilder.Append(' ');
                signatureBuilder.Append(rawSlice);
            }

            endLineIndex = lineIndex;
            endColumn = lineEndColumn >= lineStartColumn ? lineEndColumn : sanitizedLine.Length - 1;

            clause = clauseBuilder.ToString().Trim();
            if (!clause.StartsWith("export", StringComparison.Ordinal))
                break;

            var clauseRemainder = SkipJavaScriptTypeScriptTypeOnlyExportModifier(clause.AsSpan("export".Length).TrimStart());
            if (clauseRemainder.Length == 0 || clauseRemainder[0] != '*')
                break;

            if (JavaScriptTypeScriptStarReExportRegex.IsMatch(clause))
            {
                signature = signatureBuilder.ToString().Trim();
                return true;
            }

            if (lineEndColumn >= lineStartColumn)
                break;
        }

        endLineIndex = startLineIndex;
        endColumn = -1;
        clause = string.Empty;
        signature = string.Empty;
        startColumnText = -1;
        return false;
    }
}
