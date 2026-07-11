using CodeIndex.Models;
using System.Text;

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

    private static bool TryCollectJavaScriptTypeScriptLocalNamedExportClause(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out int endLineIndex,
        out int endColumn,
        out string specifiers,
        out string signature,
        out int startColumnText)
    {
        endLineIndex = startLineIndex;
        endColumn = -1;
        specifiers = string.Empty;
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
        if (exportRemainder.Length > 0)
        {
            if (exportRemainder[0] == '{')
            {
                // Valid same-line local named export.
            }
            else if (IsJavaScriptTypeScriptKeywordAt(exportRemainder, 0, "type"))
            {
                var typeRemainder = exportRemainder["type".Length..].TrimStart();
                if (typeRemainder.Length > 0 && typeRemainder[0] != '{')
                {
                    startColumnText = -1;
                    return false;
                }
            }
            else
            {
                startColumnText = -1;
                return false;
            }
        }

        startColumnText = startColumn + startLineSlice.IndexOf("export", StringComparison.Ordinal);

        var specifierBuilder = new StringBuilder(EstimateJavaScriptTypeScriptStatementCapacity(sanitizedLines, startLineIndex));
        var sawOpenBrace = false;
        var braceDepth = 0;
        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);

        for (int lineIndex = startLineIndex; lineIndex < scanEndExclusive; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? startColumnText : 0;
            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (!sawOpenBrace)
                {
                    if (ch == '{')
                    {
                        sawOpenBrace = true;
                        braceDepth = 1;
                    }

                    continue;
                }

                if (ch == '{')
                {
                    braceDepth++;
                    specifierBuilder.Append(ch);
                    continue;
                }

                if (ch == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        if (TryFindNextJavaScriptTypeScriptNonWhitespace(
                                sanitizedLines,
                                lineIndex,
                                column + 1,
                                scanEndExclusive,
                                out var nextLineIndex,
                                out var nextColumn)
                            && IsJavaScriptTypeScriptKeywordAt(sanitizedLines[nextLineIndex], nextColumn, "from")
                            && TryFindJavaScriptTypeScriptReExportModuleQuote(
                                rawLines,
                                sanitizedLines,
                                nextLineIndex,
                                scanEndExclusive - 1,
                                nextColumn + "from".Length,
                                out _,
                                out _))
                        {
                            startColumnText = -1;
                            specifiers = string.Empty;
                            return false;
                        }

                        endLineIndex = lineIndex;
                        endColumn = column;
                        if (TryFindNextJavaScriptTypeScriptNonWhitespace(
                                sanitizedLines,
                                lineIndex,
                                column + 1,
                                Math.Min(sanitizedLines.Length, lineIndex + 2),
                                out var semicolonLineIndex,
                                out var semicolonColumn)
                            && semicolonLineIndex == lineIndex
                            && sanitizedLines[semicolonLineIndex][semicolonColumn] == ';')
                        {
                            endColumn = semicolonColumn;
                        }

                        specifiers = specifierBuilder.ToString();
                        signature = BuildJavaScriptTypeScriptStatementSignature(
                            rawLines,
                            startLineIndex,
                            startColumnText,
                            endLineIndex,
                            endColumn);
                        return specifiers.AsSpan().Trim().Length > 0;
                    }

                    if (braceDepth < 0)
                    {
                        startColumnText = -1;
                        return false;
                    }

                    specifierBuilder.Append(ch);
                    continue;
                }

                if (braceDepth > 0)
                    specifierBuilder.Append(ch);
            }

            if (sawOpenBrace && braceDepth > 0)
                specifierBuilder.Append('\n');
        }

        startColumnText = -1;
        return false;
    }
}
