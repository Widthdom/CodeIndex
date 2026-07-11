using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptDynamicImportSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        var rawLine = rawLines[lineIndex];
        var sanitizedLine = sanitizedLines[lineIndex];
        var searchStart = 0;
        while (searchStart < sanitizedLine.Length)
        {
            var importIndex = sanitizedLine.IndexOf("import", searchStart, StringComparison.Ordinal);
            if (importIndex < 0)
                return;

            searchStart = importIndex + "import".Length;

            if (importIndex > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[importIndex - 1]))
                continue;

            if (searchStart < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[searchStart]))
                continue;

            if (IsJavaScriptTypeScriptPropertyAccessImportPrefix(sanitizedLine, importIndex))
                continue;

            var prefixEnd = importIndex;
            while (prefixEnd > 0 && char.IsWhiteSpace(sanitizedLine[prefixEnd - 1]))
                prefixEnd--;

            var tokenStart = prefixEnd;
            while (tokenStart > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenStart - 1]))
                tokenStart--;

            // Skip type-query contexts like `typeof import("./mod")`; only real runtime
            // dynamic imports should create the module-name symbol target.
            if (tokenStart < prefixEnd)
            {
                var precedingToken = sanitizedLine[tokenStart..prefixEnd];
                if (precedingToken is "typeof" or "keyof")
                    continue;
            }

            if (!TryReadJavaScriptTypeScriptDynamicImportModule(
                    rawLines,
                    sanitizedLines,
                    lineIndex,
                    searchStart,
                    out var moduleName,
                    out var moduleLineIndex,
                    out var moduleStartColumn,
                    out var signature))
            {
                continue;
            }

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                moduleLineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "import",
                    Name = ResolveJavaScriptTypeScriptModuleSpecifier(lang, filePath, projectRoot, moduleName),
                    Line = moduleLineIndex + 1,
                    StartLine = moduleLineIndex + 1,
                    StartColumn = moduleStartColumn,
                    EndLine = moduleLineIndex + 1,
                    Signature = signature,
                },
                rawLine);
        }
    }

    private static void ExtractJavaScriptTypeScriptStaticImportModuleSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        var sanitizedLine = sanitizedLines[lineIndex];
        var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
        while (statementStart >= 0)
        {
            if (!TryReadJavaScriptTypeScriptStaticImportModule(
                    rawLines,
                    sanitizedLines,
                    lineIndex,
                    statementStart,
                    out var moduleName,
                    out var moduleLineIndex,
                    out var moduleStartColumn,
                    out var endLineIndex,
                    out var endColumn,
                    out var signature))
            {
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                continue;
            }

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                moduleLineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "import",
                    Name = ResolveJavaScriptTypeScriptModuleSpecifier(lang, filePath, projectRoot, moduleName),
                    Line = moduleLineIndex + 1,
                    StartLine = moduleLineIndex + 1,
                    StartColumn = moduleStartColumn,
                    EndLine = endLineIndex + 1,
                    Signature = signature,
                },
                rawLines[lineIndex]);

            if (endLineIndex > lineIndex)
                break;

            statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, endColumn + 1);
        }
    }

    private static void ExtractJavaScriptTypeScriptRequireModuleSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        var rawLine = rawLines[lineIndex];
        var sanitizedLine = sanitizedLines[lineIndex];
        var searchStart = 0;
        while (searchStart < sanitizedLine.Length)
        {
            var requireIndex = sanitizedLine.IndexOf("require", searchStart, StringComparison.Ordinal);
            if (requireIndex < 0)
                return;

            searchStart = requireIndex + "require".Length;

            if (requireIndex > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[requireIndex - 1]))
                continue;

            if (searchStart < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[searchStart]))
                continue;

            if (IsJavaScriptTypeScriptPropertyAccessImportPrefix(sanitizedLine, requireIndex))
                continue;

            if (IsJavaScriptTypeScriptImportEqualsRequirePrefix(sanitizedLine, requireIndex))
                continue;

            if (!TryReadJavaScriptTypeScriptRequireModule(
                        rawLines,
                        sanitizedLines,
                        lineIndex,
                        searchStart,
                        out var moduleName,
                        out var moduleLineIndex,
                        out var moduleStartColumn,
                        out var endLineIndex,
                        out var signature)
                && !TryReadJavaScriptTypeScriptRequireResolveModule(
                    rawLines,
                    sanitizedLines,
                    lineIndex,
                    searchStart,
                    out moduleName,
                    out moduleLineIndex,
                    out moduleStartColumn,
                    out endLineIndex,
                    out signature))
            {
                continue;
            }

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                moduleLineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "import",
                    Name = ResolveJavaScriptTypeScriptModuleSpecifier(lang, filePath, projectRoot, moduleName),
                    Line = moduleLineIndex + 1,
                    StartLine = moduleLineIndex + 1,
                    StartColumn = moduleStartColumn,
                    EndLine = endLineIndex + 1,
                    Signature = signature,
                },
                rawLine);
        }
    }

    private static bool IsJavaScriptTypeScriptImportEqualsRequirePrefix(string sanitizedLine, int requireIndex)
    {
        if (requireIndex <= 0 || requireIndex > sanitizedLine.Length)
            return false;

        var prefixEnd = FindJavaScriptTypeScriptTrimmedEndExclusive(sanitizedLine, requireIndex);
        if (prefixEnd == 0 || sanitizedLine[prefixEnd - 1] != '=')
            return false;

        var importStart = 0;
        while (importStart < prefixEnd - 1 && char.IsWhiteSpace(sanitizedLine[importStart]))
            importStart++;

        return IsJavaScriptTypeScriptKeywordAt(sanitizedLine, importStart, "import");
    }

    private static bool TryReadJavaScriptTypeScriptRequireResolveModule(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int afterRequireColumn,
        out string moduleName,
        out int moduleLineIndex,
        out int moduleStartColumn,
        out int endLineIndex,
        out string signature)
    {
        moduleName = string.Empty;
        moduleLineIndex = -1;
        moduleStartColumn = -1;
        endLineIndex = -1;
        signature = string.Empty;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterRequireColumn,
                scanEndExclusive,
                out var dotLineIndex,
                out var dotColumn)
            || sanitizedLines[dotLineIndex][dotColumn] != '.')
        {
            return false;
        }

        var resolveColumn = dotColumn + 1;
        if (!IsJavaScriptTypeScriptKeywordAt(sanitizedLines[dotLineIndex], resolveColumn, "resolve"))
            return false;

        return TryReadJavaScriptTypeScriptRequireModule(
            rawLines,
            sanitizedLines,
            dotLineIndex,
            resolveColumn + "resolve".Length,
            out moduleName,
            out moduleLineIndex,
            out moduleStartColumn,
            out endLineIndex,
            out signature,
            allowTrailingArguments: true);
    }

    private static bool IsJavaScriptTypeScriptPropertyAccessImportPrefix(string sanitizedLine, int importIndex)
    {
        var prefixEnd = importIndex;
        while (prefixEnd > 0 && char.IsWhiteSpace(sanitizedLine[prefixEnd - 1]))
            prefixEnd--;

        if (prefixEnd <= 0)
            return false;

        if (sanitizedLine[prefixEnd - 1] == '#')
            return true;

        if (sanitizedLine[prefixEnd - 1] != '.')
            return false;

        var dotRunStart = prefixEnd - 1;
        while (dotRunStart > 0 && sanitizedLine[dotRunStart - 1] == '.')
            dotRunStart--;

        var dotRunLength = prefixEnd - dotRunStart;
        return dotRunLength < 3;
    }

    private static void ExtractJavaScriptTypeScriptNewUrlModuleSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        var rawLine = rawLines[lineIndex];
        var sanitizedLine = sanitizedLines[lineIndex];
        var searchStart = 0;
        while (searchStart < sanitizedLine.Length)
        {
            var urlIndex = sanitizedLine.IndexOf("URL", searchStart, StringComparison.Ordinal);
            if (urlIndex < 0)
                return;

            searchStart = urlIndex + "URL".Length;

            if (urlIndex > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[urlIndex - 1]))
                continue;

            if (searchStart < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[searchStart]))
                continue;

            var prefixEnd = urlIndex;
            while (prefixEnd > 0 && char.IsWhiteSpace(sanitizedLine[prefixEnd - 1]))
                prefixEnd--;

            var tokenStart = prefixEnd;
            while (tokenStart > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenStart - 1]))
                tokenStart--;

            if (tokenStart >= prefixEnd || sanitizedLine[tokenStart..prefixEnd] != "new")
                continue;

            if (!TryReadJavaScriptTypeScriptNewUrlModule(
                    rawLines,
                    sanitizedLines,
                    lineIndex,
                    searchStart,
                    out var moduleName,
                    out var moduleLineIndex,
                    out var moduleStartColumn,
                    out var endLineIndex,
                    out var signature))
            {
                continue;
            }

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                moduleLineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "import",
                    Name = ResolveJavaScriptTypeScriptModuleSpecifier(lang, filePath, projectRoot, moduleName),
                    Line = moduleLineIndex + 1,
                    StartLine = moduleLineIndex + 1,
                    StartColumn = moduleStartColumn,
                    EndLine = endLineIndex + 1,
                    Signature = signature,
                },
                rawLine);
        }
    }

    private static bool TryReadJavaScriptTypeScriptNewUrlModule(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int afterUrlColumn,
        out string moduleName,
        out int moduleLineIndex,
        out int moduleStartColumn,
        out int endLineIndex,
        out string signature)
    {
        moduleName = string.Empty;
        moduleLineIndex = -1;
        moduleStartColumn = -1;
        endLineIndex = -1;
        signature = string.Empty;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterUrlColumn,
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
                out moduleLineIndex,
                out var moduleQuoteColumn))
        {
            return false;
        }

        var sanitizedQuote = sanitizedLines[moduleLineIndex][moduleQuoteColumn];
        if (sanitizedQuote is not '\'' and not '"' and not '`')
            return false;

        if (!TryReadJavaScriptTypeScriptQuotedModuleName(
                rawLines,
                moduleLineIndex,
                moduleQuoteColumn,
                sanitizedQuote,
                out moduleName,
                out moduleStartColumn,
                out var moduleEndColumn))
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                moduleLineIndex,
                moduleEndColumn + 1,
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
                out var importMetaLineIndex,
                out var importMetaColumn)
            || !sanitizedLines[importMetaLineIndex].AsSpan(importMetaColumn).StartsWith("import.meta.url", StringComparison.Ordinal))
        {
            return false;
        }

        var afterImportMetaColumn = importMetaColumn + "import.meta.url".Length;
        if (afterImportMetaColumn < sanitizedLines[importMetaLineIndex].Length
            && (IsJavaScriptTypeScriptIdentifierPart(sanitizedLines[importMetaLineIndex][afterImportMetaColumn])
                || sanitizedLines[importMetaLineIndex][afterImportMetaColumn] == '.'))
        {
            return false;
        }

        if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                sanitizedLines,
                openParenLineIndex,
                openParenColumn,
                scanEndExclusive,
                out endLineIndex,
                out var closeParenColumn))
        {
            return false;
        }

        signature = BuildJavaScriptTypeScriptDynamicImportSignature(rawLines, startLineIndex, endLineIndex, closeParenColumn);
        return moduleName.Length > 0;
    }
}
