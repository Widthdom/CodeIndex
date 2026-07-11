using System.Text;
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

    private static bool TryCollectJavaScriptTypeScriptNamedReExportClause(
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
        if (exportRemainder.Length > 0)
        {
            if (exportRemainder[0] == '{')
            {
                // Valid same-line named re-export.
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
            if (JavaScriptTypeScriptNamedReExportRegex.IsMatch(clause))
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

    private static ReadOnlySpan<char> SkipJavaScriptTypeScriptTypeOnlyExportModifier(ReadOnlySpan<char> exportRemainder)
    {
        if (IsJavaScriptTypeScriptKeywordAt(exportRemainder, 0, "type"))
            return exportRemainder["type".Length..].TrimStart();

        return exportRemainder;
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
}
