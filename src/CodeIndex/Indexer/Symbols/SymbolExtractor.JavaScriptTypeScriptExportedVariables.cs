using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptExportedVariableSymbols(
        long fileId,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var exportedSymbolNames = BuildJavaScriptTypeScriptExportedSymbolNameSet(symbols);

        for (int i = 0; i < sanitizedLines.Length; i++)
        {
            var sanitizedLine = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (statementStart >= 0)
            {
                var statementSlice = sanitizedLine[statementStart..];
                var match = JavaScriptTypeScriptExportedVariableDeclarationRegex.Match(statementSlice);
                if (!match.Success)
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var absoluteMatchIndex = statementStart + match.Index;
                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, absoluteMatchIndex, sanitizedLine, includeBlockScope: false))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var declaratorStartColumn = absoluteMatchIndex + match.Length;
                if (!TryCollectJavaScriptTypeScriptExportedVariableNames(
                        sanitizedLines,
                        i,
                        declaratorStartColumn,
                        out var endLineIndex,
                        out var endColumn,
                        out var variableNames))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var signature = BuildJavaScriptTypeScriptStatementSignature(
                    rawLines,
                    i,
                    absoluteMatchIndex,
                    endLineIndex,
                    endColumn);
                foreach (var variableName in variableNames)
                {
                    if (!exportedSymbolNames.Add(variableName.Name))
                        continue;

                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        variableName.LineIndex + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = variableName.Name,
                            Line = variableName.LineIndex + 1,
                            StartLine = i + 1,
                            StartColumn = variableName.Column,
                            EndLine = endLineIndex + 1,
                            Signature = signature,
                            Visibility = "export",
                        },
                        rawLines[variableName.LineIndex]);
                }

                if (endLineIndex > i)
                    i = endLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], endColumn + 1);
                sanitizedLine = sanitizedLines[i];
            }
        }
    }

    private static HashSet<string> BuildJavaScriptTypeScriptExportedSymbolNameSet(IReadOnlyList<SymbolRecord> symbols)
    {
        var exportedSymbolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Visibility == "export")
                exportedSymbolNames.Add(symbol.Name);
        }

        return exportedSymbolNames;
    }

    private static bool TryCollectJavaScriptTypeScriptExportedVariableNames(
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out int endLineIndex,
        out int endColumn,
        out List<JavaScriptTypeScriptExportedVariableName> variableNames)
    {
        variableNames = null!;
        endLineIndex = startLineIndex;
        endColumn = Math.Max(0, startColumn);

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var expectingName = true;
        var sawTopLevelSemicolon = false;
        var scanLimit = Math.Min(sanitizedLines.Length, startLineIndex + 32);
        List<JavaScriptTypeScriptExportedVariableName>? collectedVariableNames = null;

        for (var lineIndex = startLineIndex; lineIndex < scanLimit; lineIndex++)
        {
            var line = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex
                ? Math.Min(Math.Max(0, startColumn), line.Length)
                : 0;

            while (column < line.Length)
            {
                var ch = line[column];
                if (expectingName)
                {
                    while (column < line.Length && char.IsWhiteSpace(line[column]))
                        column++;

                    if (column >= line.Length)
                        break;

                    ch = line[column];
                    if (ch is '{' or '[')
                        return false;

                    if (IsJavaScriptTypeScriptIdentifierStart(ch))
                    {
                        var nameStart = column;
                        column++;
                        while (column < line.Length && IsJavaScriptTypeScriptIdentifierPart(line[column]))
                            column++;

                        (collectedVariableNames ??= []).Add(new JavaScriptTypeScriptExportedVariableName(line[nameStart..column], lineIndex, nameStart));
                        expectingName = false;
                        continue;
                    }

                    expectingName = false;
                }

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
                    case ',':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            expectingName = true;
                        break;
                    case ';':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        {
                            endLineIndex = lineIndex;
                            endColumn = column;
                            sawTopLevelSemicolon = true;
                            column = line.Length;
                        }

                        break;
                }

                column++;
            }

            if (sawTopLevelSemicolon)
                break;

            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && collectedVariableNames is { Count: > 0 }
                && !expectingName
                && CanStopJavaScriptTypeScriptExportedVariableDeclarationAtLineEnd(line, out var lastContentColumn))
            {
                endLineIndex = lineIndex;
                endColumn = lastContentColumn;
                break;
            }

            endLineIndex = lineIndex;
            endColumn = Math.Max(0, line.Length - 1);
        }

        if (collectedVariableNames is null)
            return false;

        variableNames = collectedVariableNames;
        return true;
    }

    private static bool CanStopJavaScriptTypeScriptExportedVariableDeclarationAtLineEnd(
        string sanitizedLine,
        out int lastContentColumn)
    {
        var endExclusive = FindJavaScriptTypeScriptTrimmedEndExclusive(sanitizedLine, sanitizedLine.Length);
        lastContentColumn = endExclusive - 1;
        if (endExclusive == 0)
            return false;

        return sanitizedLine[lastContentColumn] is not (',' or '=' or '(' or '[' or '{' or '.' or '?' or ':' or '+' or '-' or '*' or '%' or '&' or '|' or '^' or '!' or '<' or '>');
    }

    private static int FindJavaScriptTypeScriptTrimmedEndExclusive(string text, int endExclusive)
    {
        endExclusive = Math.Clamp(endExclusive, 0, text.Length);
        while (endExclusive > 0 && char.IsWhiteSpace(text[endExclusive - 1]))
            endExclusive--;

        return endExclusive;
    }

    private readonly record struct JavaScriptTypeScriptExportedVariableName(string Name, int LineIndex, int Column);
}
