using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptLocalNamedExportSymbols(
        long fileId,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols)
    {
        for (int i = 0; i < sanitizedLines.Length; i++)
        {
            var sanitizedLine = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (statementStart >= 0)
            {
                if (!TryCollectJavaScriptTypeScriptLocalNamedExportClause(
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        out var endLineIndex,
                        out var endColumn,
                        out var specifiers,
                        out var signature,
                        out var startColumnText))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var localSpecifiers = ContainsJavaScriptTypeScriptStringLiteralSpecifierName(specifiers)
                    && TryExtractJavaScriptTypeScriptExportSpecifierListFromSignature(signature, out var rawLocalSpecifiers)
                    ? StripJavaScriptTypeScriptSpecifierComments(rawLocalSpecifiers)
                    : specifiers;
                foreach (var exportedName in ParseJavaScriptTypeScriptReExportedNames(localSpecifiers))
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
                            Signature = signature,
                            Visibility = "export",
                        },
                        rawLines[i]);
                }

                if (endLineIndex > i)
                {
                    i = endLineIndex;
                    sanitizedLine = sanitizedLines[i];
                }

                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, endColumn + 1);
            }
        }
    }
}
