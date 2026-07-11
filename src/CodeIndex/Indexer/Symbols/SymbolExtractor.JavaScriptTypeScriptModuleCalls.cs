using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptImportScriptsModuleSymbols(
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
            var importScriptsIndex = sanitizedLine.IndexOf("importScripts", searchStart, StringComparison.Ordinal);
            if (importScriptsIndex < 0)
                return;

            searchStart = importScriptsIndex + "importScripts".Length;

            if (importScriptsIndex > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[importScriptsIndex - 1]))
                continue;

            if (searchStart < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[searchStart]))
                continue;

            if (IsJavaScriptTypeScriptPropertyAccessImportPrefix(sanitizedLine, importScriptsIndex))
                continue;

            if (!TryReadJavaScriptTypeScriptImportScriptsModules(
                    rawLines,
                    sanitizedLines,
                    lineIndex,
                    searchStart,
                    out var moduleSpecifiers,
                    out var endLineIndex,
                    out var signature))
            {
                continue;
            }

            foreach (var (moduleName, moduleLineIndex, moduleStartColumn) in moduleSpecifiers)
            {
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
    }

    private static bool TryReadJavaScriptTypeScriptImportScriptsModules(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int afterImportScriptsColumn,
        out List<(string ModuleName, int LineIndex, int StartColumn)> moduleSpecifiers,
        out int endLineIndex,
        out string signature)
    {
        moduleSpecifiers = null!;
        endLineIndex = -1;
        signature = string.Empty;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterImportScriptsColumn,
                scanEndExclusive,
                out var openParenLineIndex,
                out var openParenColumn)
            || sanitizedLines[openParenLineIndex][openParenColumn] != '(')
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

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        List<(string ModuleName, int LineIndex, int StartColumn)>? collectedModuleSpecifiers = null;
        for (var currentLineIndex = openParenLineIndex; currentLineIndex <= endLineIndex; currentLineIndex++)
        {
            var sanitizedLine = sanitizedLines[currentLineIndex];
            var startColumn = currentLineIndex == openParenLineIndex ? openParenColumn + 1 : 0;
            var endColumnExclusive = currentLineIndex == endLineIndex ? closeParenColumn : sanitizedLine.Length;
            var column = startColumn;
            while (column < endColumnExclusive)
            {
                var ch = sanitizedLine[column];
                if (ch is '\'' or '"' or '`')
                {
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    {
                        if (TryReadJavaScriptTypeScriptQuotedModuleName(
                                rawLines,
                                currentLineIndex,
                                column,
                                ch,
                                out var moduleName,
                                out var moduleStartColumn,
                                out var moduleEndColumn))
                        {
                            (collectedModuleSpecifiers ??= []).Add((moduleName, currentLineIndex, moduleStartColumn));
                            column = moduleEndColumn + 1;
                            continue;
                        }
                    }

                    var probe = column;
                    if (TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref probe, out _))
                    {
                        column = probe;
                        continue;
                    }
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
                }

                column++;
            }
        }

        if (collectedModuleSpecifiers is null)
            return false;

        moduleSpecifiers = collectedModuleSpecifiers;
        signature = BuildJavaScriptTypeScriptDynamicImportSignature(rawLines, startLineIndex, endLineIndex, closeParenColumn);
        return true;
    }

    private static void ExtractJavaScriptTypeScriptServiceWorkerRegisterModuleSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(
            fileId,
            lang,
            filePath,
            projectRoot,
            rawLines,
            sanitizedLines,
            lineIndex,
            symbols,
            "navigator.serviceWorker.register");
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(
            fileId,
            lang,
            filePath,
            projectRoot,
            rawLines,
            sanitizedLines,
            lineIndex,
            symbols,
            "window.navigator.serviceWorker.register");
    }

    private static void ExtractJavaScriptTypeScriptImportMetaResolveModuleSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "import.meta.resolve");
    }

    private static void ExtractJavaScriptTypeScriptWorkletAddModuleSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "audioWorklet.addModule");
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "paintWorklet.addModule");
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "layoutWorklet.addModule");
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "animationWorklet.addModule");
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "CSS.paintWorklet.addModule");
        ExtractJavaScriptTypeScriptExactModuleCallSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "CSS.layoutWorklet.addModule");
    }

    private static void ExtractJavaScriptTypeScriptWorkerConstructorModuleSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        ExtractJavaScriptTypeScriptNewConstructorModuleSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "Worker");
        ExtractJavaScriptTypeScriptNewConstructorModuleSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "SharedWorker");
        ExtractJavaScriptTypeScriptNewConstructorModuleSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "window.Worker");
        ExtractJavaScriptTypeScriptNewConstructorModuleSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "window.SharedWorker");
        ExtractJavaScriptTypeScriptNewConstructorModuleSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "globalThis.Worker");
        ExtractJavaScriptTypeScriptNewConstructorModuleSymbols(fileId, lang, filePath, projectRoot, rawLines, sanitizedLines, lineIndex, symbols, "globalThis.SharedWorker");
    }

    private static void ExtractJavaScriptTypeScriptNewConstructorModuleSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols,
        string constructorName)
    {
        var rawLine = rawLines[lineIndex];
        var sanitizedLine = sanitizedLines[lineIndex];
        var searchStart = 0;
        while (searchStart < sanitizedLine.Length)
        {
            var constructorIndex = sanitizedLine.IndexOf(constructorName, searchStart, StringComparison.Ordinal);
            if (constructorIndex < 0)
                return;

            searchStart = constructorIndex + constructorName.Length;

            if (constructorIndex > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[constructorIndex - 1]))
                continue;

            if (searchStart < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[searchStart]))
                continue;

            var prefixEnd = constructorIndex;
            while (prefixEnd > 0 && char.IsWhiteSpace(sanitizedLine[prefixEnd - 1]))
                prefixEnd--;

            var tokenStart = prefixEnd;
            while (tokenStart > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenStart - 1]))
                tokenStart--;

            if (tokenStart >= prefixEnd || sanitizedLine[tokenStart..prefixEnd] != "new")
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
                    out var signature,
                    allowTrailingArguments: true))
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

    private static void ExtractJavaScriptTypeScriptExactModuleCallSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] rawLines,
        string[] sanitizedLines,
        int lineIndex,
        List<SymbolRecord> symbols,
        string callText)
    {
        var rawLine = rawLines[lineIndex];
        var sanitizedLine = sanitizedLines[lineIndex];
        var searchStart = 0;
        while (searchStart < sanitizedLine.Length)
        {
            var callIndex = sanitizedLine.IndexOf(callText, searchStart, StringComparison.Ordinal);
            if (callIndex < 0)
                return;

            searchStart = callIndex + callText.Length;

            if (callIndex > 0
                && (IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[callIndex - 1])
                    || sanitizedLine[callIndex - 1] == '.'))
            {
                continue;
            }

            if (searchStart < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[searchStart]))
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
                    out var signature,
                    allowTrailingArguments: true))
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
}
