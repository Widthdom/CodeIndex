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
}
