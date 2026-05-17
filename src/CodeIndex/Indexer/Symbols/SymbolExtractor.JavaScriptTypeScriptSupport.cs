using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptBareMethods(long fileId, string lang, string[] lines, List<SymbolRecord> symbols, JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var existingClassTargets = GetJavaScriptTypeScriptExistingClassScanTargets(lang, lines, symbols);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, existingClassTargets);

        var syntheticClassTargets = CollectJavaScriptTypeScriptSyntheticClassScanTargets(fileId, lang, lines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, syntheticClassTargets);

        var objectLiteralTargets = CollectJavaScriptTypeScriptObjectLiteralScanTargets(lang, lines, privateScopeColumns);
        ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, objectLiteralTargets);
        ExtractJavaScriptTypeScriptExportSurfaceSymbols(fileId, lang, lines, symbols, privateScopeColumns, objectLiteralTargets);
        ExtractJavaScriptTypeScriptQualifiedAssignments(fileId, lang, lines, symbols, privateScopeColumns);
    }

    // Scans for object literal declarations (`const obj = { ... }`, `module.exports = { ... }`
    // etc.) and builds class-body scan targets with ContainerKind="object". The class-body
    // scanner already handles method shorthand (`name()`, `get/set name()`, `*name()`,
    // `async name()`), so routing object literals through the same scanner picks up those
    // members without a separate pass. Nested function/class scopes are skipped via
    // privateScopeColumns so method bodies don't leak inner-object methods back to the top level.
    // `const obj = { ... }` や `module.exports = { ... }` 等のオブジェクトリテラル宣言を走査し、
    // ContainerKind="object" のクラスボディ用スキャンターゲットを構築する。クラスボディスキャナは
    // 既に method shorthand (`name()`, `get/set name()`, `*name()`, `async name()`) を扱うため、
    // 同じスキャナ経由でオブジェクトリテラルのメンバを抽出できる。ネストされた function/class
    // スコープは privateScopeColumns で弾き、内側のオブジェクトメンバをトップレベルに漏らさない。
    private static List<JavaScriptClassScanTarget> CollectJavaScriptTypeScriptObjectLiteralScanTargets(
        string lang,
        string[] lines,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var targets = new List<JavaScriptClassScanTarget>();
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var bindingMatch = JavaScriptTypeScriptObjectLiteralBindingRegex.Match(sanitizedLine);
            Match? exportDefaultMatch = null;
            if (!bindingMatch.Success)
            {
                var edm = JavaScriptTypeScriptExportDefaultObjectLiteralRegex.Match(sanitizedLine);
                if (!edm.Success)
                    continue;
                exportDefaultMatch = edm;
            }
            var match = exportDefaultMatch ?? bindingMatch;
            var isExportDefault = exportDefaultMatch != null;

            // Skip declarations nested inside a function/class body, and — for non-exported
            // const/let bindings — also inside block scopes or namespace scopes. The object
            // literal itself may be legitimate, but its method-shorthand members are already
            // reachable via the enclosing scope, and emitting them would leak non-public names
            // to the top level. `var` stays function-scoped so block-scope skip is not applied;
            // `module.exports` / `exports.X` / `export const` / `export default` are treated as
            // exported and kept.
            // function/class 本体内のネストした宣言はスキップする。加えて非 export の const/let は
            // ブロックスコープや namespace スコープも private 扱いにする。var は function スコープのため
            // ブロックスコープは除外せず、module.exports / exports.X / export const / export default は
            // export 扱いで維持する。
            var includeBlockScope = !isExportDefault
                && bindingMatch.Groups["bindingKind"].Success
                && bindingMatch.Groups["bindingKind"].Value is "const" or "let";
            if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, match.Index, sanitizedLine, includeBlockScope))
                continue;

            var isExported = isExportDefault
                || TryGetGroup(bindingMatch, "visibility") == "export"
                || bindingMatch.Groups["exportsAlias"].Success
                || bindingMatch.Groups["moduleExportsAlias"].Success
                || bindingMatch.Groups["bracketName"].Success
                || bindingMatch.Groups["moduleExports"].Success;
            if (!isExported
                && IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, match.Index, sanitizedLine))
            {
                continue;
            }

            if (!TryFindJavaScriptTypeScriptObjectLiteralOpenBrace(
                    lines,
                    i,
                    match.Index + match.Length,
                    sanitizedLine,
                    lexState,
                    out var openBraceLineIndex,
                    out var openBraceColumn))
            {
                continue;
            }

            var (_, bodyStartLine, bodyEndLine) = ResolveRange(lines, openBraceLineIndex, BodyStyle.Brace, lang, openBraceColumn);
            if (bodyStartLine == null || bodyEndLine == null)
                continue;

            var containerName = isExportDefault
                ? "default"
                : (TryGetGroup(bindingMatch, "alias")
                    ?? TryGetGroup(bindingMatch, "exportsAlias")
                    ?? TryGetGroup(bindingMatch, "moduleExportsAlias")
                    ?? (bindingMatch.Groups["moduleExports"].Success ? "module.exports" : null)
                    ?? "object");

            var candidate = CreateJavaScriptClassScanTarget(
                lines,
                lang,
                i,
                match.Index,
                bodyStartLine,
                bodyEndLine,
                containerKind: "object",
                containerName: containerName,
                isExported: isExported);

            if (!targets.Any(t => t.StartIndex == candidate.StartIndex
                && t.ScanStartIndex == candidate.ScanStartIndex
                && t.ScanEndExclusive == candidate.ScanEndExclusive
                && t.ContainerName == candidate.ContainerName))
            {
                targets.Add(candidate);
            }
        }

        return targets
            .OrderBy(t => t.StartIndex)
            .ThenByDescending(t => t.ScanEndExclusive)
            .ToList();
    }

    private static void ExtractJavaScriptTypeScriptExportSurfaceSymbols(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns,
        List<JavaScriptClassScanTarget> objectLiteralTargets)
    {
        var sanitizedLines = BuildJavaScriptTypeScriptSanitizedLines(lines);
        ExtractJavaScriptTypeScriptReExportSymbols(fileId, lang, lines, sanitizedLines, symbols);
        ExtractJavaScriptTypeScriptLocalNamedExportSymbols(fileId, lines, sanitizedLines, symbols);
        ExtractJavaScriptTypeScriptExportedVariableSymbols(fileId, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptDefaultExportArrowFunctionSymbols(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptDestructuredNamedExports(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsNamedExportAssignments(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsDefaultFunctionAssignments(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsDefinePropertyExports(fileId, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsDefinePropertiesExports(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptCommonJsObjectAssignExports(fileId, lang, lines, sanitizedLines, symbols, privateScopeColumns);
        ExtractJavaScriptTypeScriptExportedObjectLiteralProperties(fileId, lines, sanitizedLines, symbols, objectLiteralTargets);
    }

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

        var prefix = sanitizedLine[..requireIndex].TrimEnd();
        if (prefix.Length == 0 || prefix[^1] != '=')
            return false;

        var beforeEquals = prefix[..^1].TrimStart();
        return IsJavaScriptTypeScriptKeywordAt(beforeEquals, 0, "import");
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
        moduleSpecifiers = [];
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
                            moduleSpecifiers.Add((moduleName, currentLineIndex, moduleStartColumn));
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

        signature = BuildJavaScriptTypeScriptDynamicImportSignature(rawLines, startLineIndex, endLineIndex, closeParenColumn);
        return moduleSpecifiers.Count > 0;
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

    private static bool TryReadJavaScriptTypeScriptStaticImportModule(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out string moduleName,
        out int moduleLineIndex,
        out int moduleStartColumn,
        out int endLineIndex,
        out int endColumn,
        out string signature)
    {
        moduleName = string.Empty;
        moduleLineIndex = -1;
        moduleStartColumn = -1;
        endLineIndex = -1;
        endColumn = -1;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        var importColumn = SkipWhitespace(startLine, startColumn);
        if (!IsJavaScriptTypeScriptKeywordAt(startLine, importColumn, "import"))
            return false;

        var afterImportColumn = importColumn + "import".Length;
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterImportColumn,
                Math.Min(sanitizedLines.Length, startLineIndex + 16),
                out var nextLineIndex,
                out var nextColumn))
        {
            return false;
        }

        var nextChar = sanitizedLines[nextLineIndex][nextColumn];
        if (nextChar is '(' or '.')
            return false;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);
        var quoteLineIndex = -1;
        var quoteColumn = -1;

        if (nextChar is '\'' or '"')
        {
            quoteLineIndex = nextLineIndex;
            quoteColumn = nextColumn;
        }
        else
        {
            if (!TryFindJavaScriptTypeScriptStaticImportFromKeyword(
                    sanitizedLines,
                    startLineIndex,
                    afterImportColumn,
                    scanEndExclusive,
                    out var fromLineIndex,
                    out var fromColumn)
                || !TryFindNextJavaScriptTypeScriptNonWhitespace(
                    sanitizedLines,
                    fromLineIndex,
                    fromColumn + "from".Length,
                    scanEndExclusive,
                    out quoteLineIndex,
                    out quoteColumn))
            {
                return false;
            }
        }

        var sanitizedQuote = sanitizedLines[quoteLineIndex][quoteColumn];
        if (sanitizedQuote is not '\'' and not '"')
            return false;

        if (!TryReadJavaScriptTypeScriptQuotedModuleName(
                rawLines,
                quoteLineIndex,
                quoteColumn,
                sanitizedQuote,
                out moduleName,
                out moduleStartColumn,
                out var moduleEndColumn))
        {
            return false;
        }

        if (!TryFindJavaScriptTypeScriptStaticImportEnd(
                sanitizedLines,
                quoteLineIndex,
                moduleEndColumn + 1,
                scanEndExclusive,
                out endLineIndex,
                out endColumn))
        {
            return false;
        }

        moduleLineIndex = quoteLineIndex;
        signature = BuildJavaScriptTypeScriptStatementSignature(rawLines, startLineIndex, importColumn, endLineIndex, endColumn);
        return moduleName.Length > 0;
    }

    private static bool TryReadJavaScriptTypeScriptRequireModule(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int afterRequireColumn,
        out string moduleName,
        out int moduleLineIndex,
        out int moduleStartColumn,
        out int endLineIndex,
        out string signature,
        bool allowTrailingArguments = false)
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
                out var afterSpecifierLineIndex,
                out var afterSpecifierColumn))
        {
            return false;
        }

        int closeParenLineIndex;
        int closeParenColumn;
        var afterSpecifierChar = sanitizedLines[afterSpecifierLineIndex][afterSpecifierColumn];
        if (afterSpecifierChar == ')')
        {
            closeParenLineIndex = afterSpecifierLineIndex;
            closeParenColumn = afterSpecifierColumn;
        }
        else if (allowTrailingArguments && afterSpecifierChar == ',')
        {
            if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                    sanitizedLines,
                    openParenLineIndex,
                    openParenColumn,
                    scanEndExclusive,
                    out closeParenLineIndex,
                    out closeParenColumn)
                || closeParenLineIndex < afterSpecifierLineIndex
                || (closeParenLineIndex == afterSpecifierLineIndex && closeParenColumn < afterSpecifierColumn))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        endLineIndex = closeParenLineIndex;
        signature = BuildJavaScriptTypeScriptDynamicImportSignature(
            rawLines,
            startLineIndex,
            closeParenLineIndex,
            closeParenColumn);
        return moduleName.Length > 0;
    }

    private static bool TryFindJavaScriptTypeScriptStaticImportFromKeyword(
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        int endLineExclusive,
        out int fromLineIndex,
        out int fromColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var lineIndex = startLineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            while (column < line.Length)
            {
                var ch = line[column];
                if (parenDepth == 0
                    && bracketDepth == 0
                    && braceDepth == 0
                    && IsJavaScriptTypeScriptKeywordAt(line, column, "from"))
                {
                    fromLineIndex = lineIndex;
                    fromColumn = column;
                    return true;
                }

                if (ch == ';' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                    break;

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

        fromLineIndex = -1;
        fromColumn = -1;
        return false;
    }

    private static bool TryReadJavaScriptTypeScriptQuotedModuleName(
        string[] rawLines,
        int quoteLineIndex,
        int quoteColumn,
        char quoteChar,
        out string moduleName,
        out int moduleStartColumn,
        out int moduleEndColumn)
    {
        moduleName = string.Empty;
        moduleStartColumn = -1;
        moduleEndColumn = -1;

        var rawModuleLine = rawLines[quoteLineIndex];
        if (quoteColumn >= rawModuleLine.Length || rawModuleLine[quoteColumn] != quoteChar)
            return false;

        moduleStartColumn = quoteColumn + 1;
        moduleEndColumn = moduleStartColumn;
        while (moduleEndColumn < rawModuleLine.Length)
        {
            if (rawModuleLine[moduleEndColumn] == '\\' && moduleEndColumn + 1 < rawModuleLine.Length)
            {
                moduleEndColumn += 2;
                continue;
            }

            if (rawModuleLine[moduleEndColumn] == quoteChar)
                break;

            moduleEndColumn++;
        }

        if (moduleEndColumn <= moduleStartColumn
            || moduleEndColumn >= rawModuleLine.Length
            || rawModuleLine[moduleEndColumn] != quoteChar)
        {
            return false;
        }

        moduleName = rawModuleLine.Substring(moduleStartColumn, moduleEndColumn - moduleStartColumn);
        if (quoteChar == '`'
            && ContainsJavaScriptTypeScriptTemplateInterpolation(rawModuleLine, moduleStartColumn, moduleEndColumn))
        {
            moduleName = string.Empty;
            return false;
        }

        return true;
    }

    private static bool ContainsJavaScriptTypeScriptTemplateInterpolation(string rawLine, int startColumn, int endColumn)
    {
        for (var column = Math.Max(0, startColumn); column + 1 < endColumn && column + 1 < rawLine.Length; column++)
        {
            if (rawLine[column] != '$' || rawLine[column + 1] != '{')
                continue;

            var backslashCount = 0;
            for (var probe = column - 1; probe >= startColumn && rawLine[probe] == '\\'; probe--)
                backslashCount++;

            if (backslashCount % 2 == 0)
                return true;
        }

        return false;
    }

    private static bool TryFindJavaScriptTypeScriptStaticImportEnd(
        string[] sanitizedLines,
        int startScanLineIndex,
        int startScanColumn,
        int endLineExclusive,
        out int endLineIndex,
        out int endColumn)
    {
        endLineIndex = startScanLineIndex;
        endColumn = Math.Max(0, startScanColumn - 1);

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var sawAttributeKeyword = false;
        var sawAttributeBrace = false;

        for (var lineIndex = startScanLineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = sanitizedLines[lineIndex];
            var column = lineIndex == startScanLineIndex ? Math.Max(0, startScanColumn) : 0;
            var lastNonWhitespaceColumn = -1;

            while (column < line.Length)
            {
                var ch = line[column];
                if (!char.IsWhiteSpace(ch))
                    lastNonWhitespaceColumn = column;

                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                {
                    if (ch == ';')
                    {
                        endLineIndex = lineIndex;
                        endColumn = column;
                        return true;
                    }

                    if (IsJavaScriptTypeScriptKeywordAt(line, column, "with")
                        || IsJavaScriptTypeScriptKeywordAt(line, column, "assert"))
                    {
                        sawAttributeKeyword = true;
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
                        if (sawAttributeKeyword)
                            sawAttributeBrace = true;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        if (sawAttributeKeyword && sawAttributeBrace && braceDepth == 0)
                        {
                            endLineIndex = lineIndex;
                            endColumn = column;
                        }
                        break;
                }

                column++;
            }

            if (lastNonWhitespaceColumn >= 0)
            {
                endLineIndex = lineIndex;
                endColumn = lastNonWhitespaceColumn;
            }

            if (!sawAttributeKeyword || (sawAttributeBrace && braceDepth == 0))
                return true;
        }

        return endLineIndex >= startScanLineIndex && endColumn >= 0;
    }

    private static bool TryReadJavaScriptTypeScriptDynamicImportModule(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int afterImportColumn,
        out string moduleName,
        out int moduleLineIndex,
        out int moduleStartColumn,
        out string signature)
    {
        moduleName = string.Empty;
        moduleLineIndex = -1;
        moduleStartColumn = -1;
        signature = string.Empty;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 16);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterImportColumn,
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
                out var afterSpecifierLineIndex,
                out var afterSpecifierColumn))
        {
            return false;
        }

        int closeParenLineIndex;
        int closeParenColumn;
        var afterSpecifierChar = sanitizedLines[afterSpecifierLineIndex][afterSpecifierColumn];
        if (afterSpecifierChar == ')')
        {
            closeParenLineIndex = afterSpecifierLineIndex;
            closeParenColumn = afterSpecifierColumn;
        }
        else if (afterSpecifierChar == ',')
        {
            if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                    sanitizedLines,
                    openParenLineIndex,
                    openParenColumn,
                    scanEndExclusive,
                    out closeParenLineIndex,
                    out closeParenColumn)
                || closeParenLineIndex < afterSpecifierLineIndex
                || (closeParenLineIndex == afterSpecifierLineIndex && closeParenColumn < afterSpecifierColumn))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        signature = BuildJavaScriptTypeScriptDynamicImportSignature(
            rawLines,
            startLineIndex,
            closeParenLineIndex,
            closeParenColumn);
        return true;
    }

    private static string BuildJavaScriptTypeScriptStatementSignature(
        string[] rawLines,
        int startLineIndex,
        int startColumn,
        int endLineIndex,
        int endColumn)
    {
        if (startLineIndex == endLineIndex)
        {
            var line = rawLines[startLineIndex];
            var start = Math.Min(Math.Max(0, startColumn), line.Length);
            var endExclusive = Math.Min(Math.Max(start, endColumn + 1), line.Length);
            return line[start..endExclusive].Trim();
        }

        var builder = new StringBuilder(EstimateJavaScriptTypeScriptLineSliceLength(rawLines, startLineIndex, endLineIndex));
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            if (lineIndex > startLineIndex)
                builder.Append('\n');

            var line = rawLines[lineIndex];
            var sliceStart = lineIndex == startLineIndex ? Math.Min(Math.Max(0, startColumn), line.Length) : 0;
            var sliceEnd = lineIndex == endLineIndex ? Math.Min(Math.Max(sliceStart, endColumn + 1), line.Length) : line.Length;
            builder.Append(line[sliceStart..sliceEnd]);
        }

        return builder.ToString().Trim();
    }

    private static bool TryFindJavaScriptTypeScriptDynamicImportCloseParen(
        string[] sanitizedLines,
        int openParenLineIndex,
        int openParenColumn,
        int endLineExclusive,
        out int closeParenLineIndex,
        out int closeParenColumn)
    {
        var parenDepth = 0;
        for (var lineIndex = openParenLineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = sanitizedLines[lineIndex];
            var column = lineIndex == openParenLineIndex ? openParenColumn : 0;
            while (column < line.Length)
            {
                var ch = line[column];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    if (parenDepth == 0)
                        break;

                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        closeParenLineIndex = lineIndex;
                        closeParenColumn = column;
                        return true;
                    }
                }

                column++;
            }
        }

        closeParenLineIndex = -1;
        closeParenColumn = -1;
        return false;
    }

    private static bool TryFindNextJavaScriptTypeScriptNonWhitespace(
        string[] lines,
        int startLineIndex,
        int startColumn,
        int endLineExclusive,
        out int lineIndex,
        out int column)
    {
        for (lineIndex = startLineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = lines[lineIndex];
            column = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            while (column < line.Length && char.IsWhiteSpace(line[column]))
                column++;

            if (column < line.Length)
                return true;
        }

        lineIndex = -1;
        column = -1;
        return false;
    }

    private static string BuildJavaScriptTypeScriptDynamicImportSignature(
        string[] rawLines,
        int startLineIndex,
        int endLineIndex,
        int endColumn)
    {
        if (startLineIndex == endLineIndex)
            return rawLines[startLineIndex].Trim();

        var builder = new StringBuilder(EstimateJavaScriptTypeScriptLineSliceLength(rawLines, startLineIndex, endLineIndex));
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
        {
            if (lineIndex > startLineIndex)
                builder.Append('\n');

            var line = rawLines[lineIndex];
            var sliceEnd = lineIndex == endLineIndex ? Math.Min(endColumn + 1, line.Length) : line.Length;

            builder.Append(line[..sliceEnd]);
        }

        return builder.ToString().Trim();
    }

    private static int EstimateJavaScriptTypeScriptLineSliceLength(string[] lines, int startLineIndex, int endLineIndex)
    {
        if (lines.Length == 0)
            return 0;

        startLineIndex = Math.Clamp(startLineIndex, 0, lines.Length - 1);
        endLineIndex = Math.Clamp(endLineIndex, startLineIndex, lines.Length - 1);

        var capacity = 0;
        for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
            capacity += lines[lineIndex].Length + (lineIndex > startLineIndex ? 1 : 0);

        return capacity;
    }

    private static int EstimateJavaScriptTypeScriptStatementCapacity(string[] lines, int startLineIndex)
    {
        if (lines.Length == 0)
            return 0;

        startLineIndex = Math.Clamp(startLineIndex, 0, lines.Length - 1);
        var endExclusive = Math.Min(lines.Length, startLineIndex + 16);
        var capacity = 0;
        for (var lineIndex = startLineIndex; lineIndex < endExclusive; lineIndex++)
            capacity += lines[lineIndex].Length + (lineIndex > startLineIndex ? 1 : 0);

        return capacity;
    }

    private static bool TryHandleJavaScriptTypeScriptImportEqualsLine(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string rawLine,
        int lineNumber,
        List<SymbolRecord> symbols)
    {
        var cursor = SkipWhitespace(rawLine, 0);
        if (!rawLine.AsSpan(cursor).StartsWith("import", StringComparison.Ordinal))
            return false;

        var importEnd = cursor + "import".Length;
        if (importEnd < rawLine.Length && (char.IsLetterOrDigit(rawLine[importEnd]) || rawLine[importEnd] is '_' or '$'))
            return false;

        cursor = SkipWhitespace(rawLine, importEnd);

        var aliasStart = cursor;
        if (aliasStart >= rawLine.Length
            || !(char.IsLetter(rawLine[aliasStart]) || rawLine[aliasStart] is '_' or '$'))
        {
            return false;
        }

        cursor++;
        while (cursor < rawLine.Length && (char.IsLetterOrDigit(rawLine[cursor]) || rawLine[cursor] is '_' or '$'))
            cursor++;

        var aliasName = rawLine[aliasStart..cursor];
        cursor = SkipWhitespace(rawLine, cursor);
        if (cursor >= rawLine.Length || rawLine[cursor] != '=')
            return false;

        cursor = SkipWhitespace(rawLine, cursor + 1);
        if (!rawLine.AsSpan(cursor).StartsWith("require", StringComparison.Ordinal))
            return false;

        var requireEnd = cursor + "require".Length;
        if (requireEnd < rawLine.Length && (char.IsLetterOrDigit(rawLine[requireEnd]) || rawLine[requireEnd] is '_' or '$'))
            return false;

        cursor = SkipWhitespace(rawLine, requireEnd);
        if (cursor >= rawLine.Length || rawLine[cursor] != '(')
            return false;

        cursor = SkipWhitespace(rawLine, cursor + 1);
        if (cursor >= rawLine.Length || rawLine[cursor] is not '\'' and not '"')
            return false;

        var quote = rawLine[cursor];
        var moduleLiteralStart = cursor + 1;
        cursor = moduleLiteralStart;
        while (cursor < rawLine.Length)
        {
            if (rawLine[cursor] == '\\' && cursor + 1 < rawLine.Length)
            {
                cursor += 2;
                continue;
            }

            if (rawLine[cursor] == quote)
                break;

            cursor++;
        }

        if (cursor >= rawLine.Length || rawLine[cursor] != quote)
            return false;

        var moduleName = rawLine[moduleLiteralStart..cursor];
        cursor++;
        cursor = SkipWhitespace(rawLine, cursor);
        if (cursor >= rawLine.Length || rawLine[cursor] != ')')
            return false;

        cursor++;
        cursor = SkipWhitespace(rawLine, cursor);
        if (cursor < rawLine.Length && rawLine[cursor] == ';')
        {
            cursor++;
            cursor = SkipWhitespace(rawLine, cursor);
        }

        if (cursor < rawLine.Length)
        {
            if (rawLine.AsSpan(cursor).StartsWith("//", StringComparison.Ordinal))
            {
                cursor = rawLine.Length;
            }
            else if (rawLine.AsSpan(cursor).StartsWith("/*", StringComparison.Ordinal))
            {
                var commentEnd = rawLine.IndexOf("*/", cursor, StringComparison.Ordinal);
                if (commentEnd < 0)
                    return false;

                cursor = SkipWhitespace(rawLine, commentEnd + 2);
            }
            else
            {
                return false;
            }
        }

        if (cursor < rawLine.Length)
            return false;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "import",
                Name = aliasName,
                Line = lineNumber,
                StartLine = lineNumber,
                StartColumn = aliasStart,
                EndLine = lineNumber,
                Signature = rawLine.Trim(),
            },
            rawLine);

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "import",
                Name = ResolveJavaScriptTypeScriptModuleSpecifier(lang, filePath, projectRoot, moduleName),
                Line = lineNumber,
                StartLine = lineNumber,
                StartColumn = moduleLiteralStart,
                EndLine = lineNumber,
                Signature = rawLine.Trim(),
            },
            rawLine);

        return true;
    }

    private static void ExtractJavaScriptTypeScriptQualifiedAssignments(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var sanitizedLines = BuildJavaScriptTypeScriptSanitizedLines(lines);
        var syntheticClassTargets = new List<JavaScriptClassScanTarget>();

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
                        syntheticClassTargets,
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

                var kind = StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    ? "function"
                    : "property";

                int? bodyStartLine = null;
                int? bodyEndLine = null;
                if (kind == "function"
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

        if (syntheticClassTargets.Count > 0)
            ExtractJavaScriptTypeScriptBareMethodsInTargets(fileId, lang, lines, symbols, syntheticClassTargets);
    }

    private static string[] BuildJavaScriptTypeScriptSanitizedLines(string[] lines)
    {
        var sanitizedLines = new string[lines.Length];
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            sanitizedLines[i] = lexedLine.SanitizedLine;
            lexState = lexedLine.EndState;
        }

        return sanitizedLines;
    }

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

        var startLineSlice = startLine[startColumn..];
        var trimmedStartLine = startLineSlice.TrimStart();
        if (trimmedStartLine.Length == 0
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

            var sanitizedSlice = sanitizedLine[lineStartColumn..lineEndExclusive].Trim();
            if (sanitizedSlice.Length > 0)
            {
                if (clauseBuilder.Length > 0)
                    clauseBuilder.Append(' ');
                clauseBuilder.Append(sanitizedSlice);
            }

            var rawSlice = rawLine[lineStartColumn..Math.Min(rawLine.Length, lineEndExclusive)].Trim();
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

            var clauseRemainder = SkipJavaScriptTypeScriptTypeOnlyExportModifier(clause["export".Length..].TrimStart());
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

        var startLineSlice = startLine[startColumn..];
        var trimmedStartLine = startLineSlice.TrimStart();
        if (trimmedStartLine.Length == 0
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
            else if (exportRemainder.StartsWith("type", StringComparison.Ordinal))
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

            var sanitizedSlice = sanitizedLine[lineStartColumn..lineEndExclusive].Trim();
            if (sanitizedSlice.Length > 0)
            {
                if (clauseBuilder.Length > 0)
                    clauseBuilder.Append(' ');
                clauseBuilder.Append(sanitizedSlice);
            }

            var rawSlice = rawLine[lineStartColumn..Math.Min(rawLine.Length, lineEndExclusive)].Trim();
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

        var startLineSlice = startLine[startColumn..];
        var trimmedStartLine = startLineSlice.TrimStart();
        if (trimmedStartLine.Length == 0
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

        var specifierBuilder = new StringBuilder();
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
                        return specifiers.Trim().Length > 0;
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

    private static void ExtractJavaScriptTypeScriptExportedVariableSymbols(
        long fileId,
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
                    if (symbols.Any(s => s.Visibility == "export" && s.Name == variableName.Name))
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

    private static bool TryCollectJavaScriptTypeScriptExportedVariableNames(
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out int endLineIndex,
        out int endColumn,
        out List<JavaScriptTypeScriptExportedVariableName> variableNames)
    {
        variableNames = [];
        endLineIndex = startLineIndex;
        endColumn = Math.Max(0, startColumn);

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var expectingName = true;
        var sawTopLevelSemicolon = false;
        var scanLimit = Math.Min(sanitizedLines.Length, startLineIndex + 32);

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

                        variableNames.Add(new JavaScriptTypeScriptExportedVariableName(line[nameStart..column], lineIndex, nameStart));
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
                && variableNames.Count > 0
                && !expectingName
                && CanStopJavaScriptTypeScriptExportedVariableDeclarationAtLineEnd(line))
            {
                endLineIndex = lineIndex;
                endColumn = Math.Max(0, line.TrimEnd().Length - 1);
                break;
            }

            endLineIndex = lineIndex;
            endColumn = Math.Max(0, line.Length - 1);
        }

        return variableNames.Count > 0;
    }

    private static bool CanStopJavaScriptTypeScriptExportedVariableDeclarationAtLineEnd(string sanitizedLine)
    {
        var trimmed = sanitizedLine.TrimEnd();
        if (trimmed.Length == 0)
            return false;

        return trimmed[^1] is not (',' or '=' or '(' or '[' or '{' or '.' or '?' or ':' or '+' or '-' or '*' or '%' or '&' or '|' or '^' or '!' or '<' or '>');
    }

    private readonly record struct JavaScriptTypeScriptExportedVariableName(string Name, int LineIndex, int Column);

    private static void ExtractJavaScriptTypeScriptDestructuredNamedExports(
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
                var match = JavaScriptTypeScriptDestructuredNamedExportRegex.Match(statementSlice);
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

                var openBraceColumn = absoluteMatchIndex + match.Value.LastIndexOf('{');
                if (!TryCollectJavaScriptTypeScriptDestructuredExportPattern(
                        lang,
                        rawLines,
                        sanitizedLines,
                        i,
                        absoluteMatchIndex,
                        openBraceColumn,
                        out var endLineIndex,
                        out var closeBraceColumn,
                        out var pattern,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                foreach (var exportedName in ParseJavaScriptTypeScriptDestructuredBindingNames(pattern))
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
                            StartColumn = absoluteMatchIndex,
                            EndLine = endLineIndex + 1,
                            Signature = signature,
                            Visibility = "export",
                        },
                        rawLines[i]);
                }

                if (endLineIndex > i)
                    i = endLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], closeBraceColumn + 1);
                sanitizedLine = sanitizedLines[i];
            }
        }
    }

    private static bool TryCollectJavaScriptTypeScriptDestructuredExportPattern(
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int exportStartColumn,
        int openBraceColumn,
        out int endLineIndex,
        out int closeBraceColumn,
        out string pattern,
        out string signature)
    {
        endLineIndex = startLineIndex;
        closeBraceColumn = -1;
        pattern = string.Empty;
        signature = string.Empty;

        var patternBuilder = new System.Text.StringBuilder();
        var signatureBuilder = new System.Text.StringBuilder();
        var braceDepth = 0;

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var rawLine = rawLines[lineIndex];
            var column = lineIndex == startLineIndex ? openBraceColumn : 0;
            if (column < 0 || column >= sanitizedLine.Length)
                return false;

            var signatureStartColumn = lineIndex == startLineIndex ? exportStartColumn : 0;
            var signatureSliceStart = Math.Min(signatureStartColumn, rawLine.Length);

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (ch == '{')
                {
                    braceDepth++;
                    if (braceDepth > 1)
                        patternBuilder.Append(ch);
                    continue;
                }

                if (ch == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        var rawSliceEnd = Math.Min(rawLine.Length, column + 1);
                        if (rawSliceEnd > signatureSliceStart)
                        {
                            if (signatureBuilder.Length > 0)
                                signatureBuilder.Append(' ');
                            signatureBuilder.Append(rawLine[signatureSliceStart..rawSliceEnd].Trim());
                        }

                        if (!HasJavaScriptTypeScriptDestructuredExportInitializer(
                                sanitizedLines,
                                lang,
                                lineIndex,
                                column + 1))
                        {
                            return false;
                        }

                        endLineIndex = lineIndex;
                        closeBraceColumn = column;
                        pattern = patternBuilder.ToString();
                        signature = signatureBuilder.ToString().Trim();
                        return true;
                    }

                    if (braceDepth < 0)
                        return false;

                    patternBuilder.Append(ch);
                    continue;
                }

                if (braceDepth > 0)
                    patternBuilder.Append(ch);
            }

            if (braceDepth > 0)
                patternBuilder.Append('\n');

            var rawSlice = rawLine[signatureSliceStart..].Trim();
            if (rawSlice.Length > 0)
            {
                if (signatureBuilder.Length > 0)
                    signatureBuilder.Append(' ');
                signatureBuilder.Append(rawSlice);
            }
        }

        return false;
    }

    private static bool HasJavaScriptTypeScriptDestructuredExportInitializer(
        string[] sanitizedLines,
        string lang,
        int startLineIndex,
        int startColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            if (column >= sanitizedLine.Length)
                continue;

            var statementEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, column, lang);
            var endExclusive = statementEndColumn >= column
                ? statementEndColumn + 1
                : sanitizedLine.Length;

            for (; column < endExclusive; column++)
            {
                var ch = sanitizedLine[column];
                if (parenDepth == 0
                    && bracketDepth == 0
                    && braceDepth == 0
                    && ch == '='
                    && (column + 1 >= sanitizedLine.Length || sanitizedLine[column + 1] != '>')
                    && (column == 0 || sanitizedLine[column - 1] is not ('=' or '!' or '<' or '>')))
                {
                    return true;
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
            }

            if (statementEndColumn >= 0)
                return false;
        }

        return false;
    }

    private static IReadOnlyList<string> ParseJavaScriptTypeScriptDestructuredBindingNames(string pattern)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectJavaScriptTypeScriptObjectBindingNames(pattern, 0, pattern.Length, names, seen);
        return names;
    }

    private static void CollectJavaScriptTypeScriptObjectBindingNames(
        string text,
        int start,
        int end,
        List<string> names,
        HashSet<string> seen)
    {
        var index = start;
        while (index < end)
        {
            index = SkipJavaScriptTypeScriptDestructuredSeparators(text, index, end);
            if (index >= end)
                return;

            if (StartsJavaScriptTypeScriptDestructuredRest(text, index))
            {
                index += 3;
                TryCollectJavaScriptTypeScriptDestructuredIdentifier(text, ref index, end, names, seen);
                index = SkipJavaScriptTypeScriptDestructuredSegment(text, index, end);
                continue;
            }

            var keyStart = index;
            string? shorthandName = null;
            if (TryReadJavaScriptTypeScriptDestructuredPropertyKey(text, ref index, end, out var keyName))
                shorthandName = keyName;
            else
                index = SkipJavaScriptTypeScriptDestructuredSegment(text, index + 1, end);

            var afterKey = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
            if (afterKey < end && text[afterKey] == ':')
            {
                index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, afterKey + 1, end);
                CollectJavaScriptTypeScriptBindingNamesFromPattern(text, ref index, end, names, seen);
            }
            else
            {
                if (shorthandName != null)
                    AddJavaScriptTypeScriptDestructuredBindingName(shorthandName, names, seen);
                index = SkipJavaScriptTypeScriptDestructuredDefaultValue(text, afterKey, end);
            }

            if (index == keyStart)
                index++;
        }
    }

    private static void CollectJavaScriptTypeScriptArrayBindingNames(
        string text,
        int start,
        int end,
        List<string> names,
        HashSet<string> seen)
    {
        var index = start;
        while (index < end)
        {
            index = SkipJavaScriptTypeScriptDestructuredSeparators(text, index, end);
            if (index >= end)
                return;

            CollectJavaScriptTypeScriptBindingNamesFromPattern(text, ref index, end, names, seen);
        }
    }

    private static void CollectJavaScriptTypeScriptBindingNamesFromPattern(
        string text,
        ref int index,
        int end,
        List<string> names,
        HashSet<string> seen)
    {
        index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
        if (index >= end)
            return;

        if (StartsJavaScriptTypeScriptDestructuredRest(text, index))
        {
            index += 3;
            TryCollectJavaScriptTypeScriptDestructuredIdentifier(text, ref index, end, names, seen);
            index = SkipJavaScriptTypeScriptDestructuredSegment(text, index, end);
            return;
        }

        if (text[index] == '{'
            && TryFindJavaScriptTypeScriptDestructuredBalancedClose(text, index, end, '{', '}', out var objectClose))
        {
            CollectJavaScriptTypeScriptObjectBindingNames(text, index + 1, objectClose, names, seen);
            index = SkipJavaScriptTypeScriptDestructuredDefaultValue(text, objectClose + 1, end);
            return;
        }

        if (text[index] == '['
            && TryFindJavaScriptTypeScriptDestructuredBalancedClose(text, index, end, '[', ']', out var arrayClose))
        {
            CollectJavaScriptTypeScriptArrayBindingNames(text, index + 1, arrayClose, names, seen);
            index = SkipJavaScriptTypeScriptDestructuredDefaultValue(text, arrayClose + 1, end);
            return;
        }

        if (TryCollectJavaScriptTypeScriptDestructuredIdentifier(text, ref index, end, names, seen))
        {
            index = SkipJavaScriptTypeScriptDestructuredDefaultValue(text, index, end);
            return;
        }

        index = SkipJavaScriptTypeScriptDestructuredSegment(text, index + 1, end);
    }

    private static bool TryReadJavaScriptTypeScriptDestructuredPropertyKey(
        string text,
        ref int index,
        int end,
        out string? keyName)
    {
        keyName = null;
        index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
        if (index >= end)
            return false;

        if (IsJavaScriptTypeScriptIdentifierStart(text[index]))
        {
            var start = index;
            index++;
            while (index < end && IsJavaScriptTypeScriptIdentifierPart(text[index]))
                index++;
            keyName = text[start..index];
            return true;
        }

        if (text[index] is '\'' or '"' or '`')
            return TrySkipJavaScriptTypeScriptDestructuredQuotedLiteral(text, ref index, end);

        if (text[index] == '['
            && TryFindJavaScriptTypeScriptDestructuredBalancedClose(text, index, end, '[', ']', out var close))
        {
            index = close + 1;
            return true;
        }

        return false;
    }

    private static bool TryCollectJavaScriptTypeScriptDestructuredIdentifier(
        string text,
        ref int index,
        int end,
        List<string> names,
        HashSet<string> seen)
    {
        index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
        if (index >= end || !IsJavaScriptTypeScriptIdentifierStart(text[index]))
            return false;

        var start = index;
        index++;
        while (index < end && IsJavaScriptTypeScriptIdentifierPart(text[index]))
            index++;

        AddJavaScriptTypeScriptDestructuredBindingName(text[start..index], names, seen);
        return true;
    }

    private static void AddJavaScriptTypeScriptDestructuredBindingName(
        string name,
        List<string> names,
        HashSet<string> seen)
    {
        if (name.Length > 0 && seen.Add(name))
            names.Add(name);
    }

    private static int SkipJavaScriptTypeScriptDestructuredWhitespace(string text, int index, int end)
    {
        while (index < end && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static int SkipJavaScriptTypeScriptDestructuredSeparators(string text, int index, int end)
    {
        while (index < end && (char.IsWhiteSpace(text[index]) || text[index] == ','))
            index++;
        return index;
    }

    private static bool StartsJavaScriptTypeScriptDestructuredRest(string text, int index) =>
        index + 2 < text.Length
        && text[index] == '.'
        && text[index + 1] == '.'
        && text[index + 2] == '.';

    private static int SkipJavaScriptTypeScriptDestructuredDefaultValue(string text, int index, int end)
    {
        index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
        if (index >= end || text[index] != '=')
            return index;

        return SkipJavaScriptTypeScriptDestructuredSegment(text, index + 1, end);
    }

    private static int SkipJavaScriptTypeScriptDestructuredSegment(string text, int index, int end)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        while (index < end)
        {
            var ch = text[index];
            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && ch == ',')
                return index + 1;

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
                case '\'':
                case '"':
                case '`':
                    TrySkipJavaScriptTypeScriptDestructuredQuotedLiteral(text, ref index, end);
                    continue;
            }

            index++;
        }

        return index;
    }

    private static bool TryFindJavaScriptTypeScriptDestructuredBalancedClose(
        string text,
        int openIndex,
        int end,
        char open,
        char close,
        out int closeIndex)
    {
        closeIndex = -1;
        var depth = 0;
        for (var index = openIndex; index < end; index++)
        {
            var ch = text[index];
            if (ch == open)
            {
                depth++;
                continue;
            }

            if (ch == close)
            {
                depth--;
                if (depth == 0)
                {
                    closeIndex = index;
                    return true;
                }

                continue;
            }

            if (ch is '\'' or '"' or '`')
            {
                TrySkipJavaScriptTypeScriptDestructuredQuotedLiteral(text, ref index, end);
                index--;
            }
        }

        return false;
    }

    private static bool TrySkipJavaScriptTypeScriptDestructuredQuotedLiteral(string text, ref int index, int end)
    {
        if (index >= end || text[index] is not ('\'' or '"' or '`'))
            return false;

        var delimiter = text[index];
        index++;
        var escapeNext = false;
        while (index < end)
        {
            var ch = text[index];
            if (escapeNext)
            {
                escapeNext = false;
                index++;
                continue;
            }

            if (ch == '\\')
            {
                escapeNext = true;
                index++;
                continue;
            }

            if (ch == delimiter)
            {
                index++;
                return true;
            }

            index++;
        }

        return false;
    }

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

                var kind = StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    ? "function"
                    : "property";

                int? bodyStartLine = null;
                int? bodyEndLine = null;
                if (kind == "function")
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

        var rawName = rawLine.Substring(startColumn, length).Trim();
        return rawName.Length == 0 ? null : rawName;
    }

    private static void ExtractJavaScriptTypeScriptDefaultExportArrowFunctionSymbols(
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
                var match = JavaScriptTypeScriptAnonymousDefaultExportRegex.Match(statementSlice);
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

                if (!TryCollectJavaScriptTypeScriptAssignedRhs(
                        rawLines,
                        sanitizedLines,
                        i,
                        absoluteMatchIndex,
                        absoluteMatchIndex + match.Length,
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

                if (!StartsJavaScriptTypeScriptArrowFunctionAssignmentValue(classificationRhs))
                {
                    if (rhsEndLineIndex > i)
                        i = rhsEndLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], rhsEndColumn + 1);
                    sanitizedLine = sanitizedLines[i];
                    continue;
                }

                int? bodyStartLine = null;
                int? bodyEndLine = null;
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

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    i + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "default",
                        Line = i + 1,
                        StartLine = i + 1,
                        StartColumn = absoluteMatchIndex,
                        EndLine = Math.Max(i + 1, bodyEndLine ?? (rhsEndLineIndex + 1)),
                        BodyStartLine = bodyStartLine,
                        BodyEndLine = bodyEndLine,
                        Signature = signature,
                        Visibility = "export",
                    },
                    rawLines[i]);

                if (rhsEndLineIndex > i)
                    i = rhsEndLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], rhsEndColumn + 1);
                sanitizedLine = sanitizedLines[i];
            }
        }
    }

    private static void ExtractJavaScriptTypeScriptCommonJsDefaultFunctionAssignments(
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
                var match = JavaScriptTypeScriptCommonJsDefaultExportAssignmentRegex.Match(statementSlice);
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

                if (!StartsJavaScriptTypeScriptFunctionAssignmentValue(classificationRhs)
                    || StartsJavaScriptTypeScriptClassAssignmentValue(classificationRhs))
                {
                    if (rhsEndLineIndex > i)
                        i = rhsEndLineIndex;
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], rhsEndColumn + 1);
                    sanitizedLine = sanitizedLines[i];
                    continue;
                }

                int? bodyStartLine = null;
                int? bodyEndLine = null;
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

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    i + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "function",
                        Name = "default",
                        Line = i + 1,
                        StartLine = i + 1,
                        StartColumn = absoluteMatchIndex,
                        EndLine = Math.Max(i + 1, bodyEndLine ?? (rhsEndLineIndex + 1)),
                        BodyStartLine = bodyStartLine,
                        BodyEndLine = bodyEndLine,
                        Signature = signature,
                        Visibility = "export",
                    },
                    rawLines[i]);

                if (rhsEndLineIndex > i)
                    i = rhsEndLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], rhsEndColumn + 1);
                sanitizedLine = sanitizedLines[i];
            }
        }
    }

    private static void ExtractJavaScriptTypeScriptCommonJsDefinePropertyExports(
        long fileId,
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
                if (!TryReadJavaScriptTypeScriptCommonJsDefinePropertyExport(
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        out var propertyName,
                        out var propertyLineIndex,
                        out var propertyStartColumn,
                        out var endLineIndex,
                        out var endColumn,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, statementStart, sanitizedLine, includeBlockScope: false)
                    || IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, statementStart, sanitizedLine))
                {
                    if (endLineIndex > i)
                    {
                        i = endLineIndex;
                        sanitizedLine = sanitizedLines[i];
                    }

                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, endColumn + 1);
                    continue;
                }

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    propertyLineIndex + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = propertyName,
                        Line = propertyLineIndex + 1,
                        StartLine = propertyLineIndex + 1,
                        StartColumn = propertyStartColumn,
                        EndLine = endLineIndex + 1,
                        Signature = signature,
                        Visibility = "export",
                    },
                    rawLines[propertyLineIndex]);

                if (endLineIndex > i)
                {
                    i = endLineIndex;
                    sanitizedLine = sanitizedLines[i];
                }

                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, endColumn + 1);
            }
        }
    }

    private static bool TryReadJavaScriptTypeScriptCommonJsDefinePropertyExport(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        out string propertyName,
        out int propertyLineIndex,
        out int propertyStartColumn,
        out int endLineIndex,
        out int endColumn,
        out string signature)
    {
        propertyName = string.Empty;
        propertyLineIndex = -1;
        propertyStartColumn = -1;
        endLineIndex = -1;
        endColumn = -1;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        var objectColumn = SkipWhitespace(startLine, startColumn);
        const string definePropertyCall = "Object.defineProperty";
        if (!startLine.AsSpan(objectColumn).StartsWith(definePropertyCall, StringComparison.Ordinal))
            return false;

        var afterCallColumn = objectColumn + definePropertyCall.Length;
        if (afterCallColumn < startLine.Length && IsJavaScriptTypeScriptIdentifierPart(startLine[afterCallColumn]))
            return false;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 32);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterCallColumn,
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
                out var targetLineIndex,
                out var targetColumn)
            || !TryReadJavaScriptTypeScriptCommonJsDefinePropertyTarget(
                sanitizedLines[targetLineIndex],
                targetColumn,
                out var targetEndColumn))
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                targetLineIndex,
                targetEndColumn,
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
                out propertyLineIndex,
                out var propertyQuoteColumn))
        {
            return false;
        }

        int propertyEndColumn;
        var sanitizedQuote = sanitizedLines[propertyLineIndex][propertyQuoteColumn];
        if (sanitizedQuote is '\'' or '"')
        {
            if (!TryReadJavaScriptTypeScriptQuotedExportPropertyName(
                    rawLines[propertyLineIndex],
                    sanitizedLines[propertyLineIndex],
                    propertyQuoteColumn,
                    out propertyName,
                    out propertyStartColumn,
                    out var quotedPropertyEndColumn))
            {
                return false;
            }

            propertyEndColumn = quotedPropertyEndColumn;
        }
        else if (char.IsDigit(sanitizedQuote))
        {
            if (!TryReadJavaScriptTypeScriptNumericExportPropertyName(
                    rawLines[propertyLineIndex],
                    sanitizedLines[propertyLineIndex],
                    propertyQuoteColumn,
                    out propertyName,
                    out propertyStartColumn,
                    out var numericPropertyEndColumn))
            {
                return false;
            }

            propertyEndColumn = numericPropertyEndColumn;
        }
        else
        {
            return false;
        }

        if (propertyName == "__esModule")
            return false;

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                propertyLineIndex,
                propertyEndColumn + 1,
                scanEndExclusive,
                out var afterPropertyLineIndex,
                out var afterPropertyColumn)
            || sanitizedLines[afterPropertyLineIndex][afterPropertyColumn] != ',')
        {
            return false;
        }

        if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                sanitizedLines,
                openParenLineIndex,
                openParenColumn,
                scanEndExclusive,
                out endLineIndex,
                out endColumn))
        {
            return false;
        }

        signature = BuildJavaScriptTypeScriptStatementSignature(rawLines, startLineIndex, objectColumn, endLineIndex, endColumn);
        return propertyName.Length > 0;
    }

    private static bool TryReadJavaScriptTypeScriptCommonJsDefinePropertyTarget(
        string sanitizedLine,
        int targetColumn,
        out int targetEndColumn)
    {
        targetEndColumn = targetColumn;

        if (IsJavaScriptTypeScriptKeywordAt(sanitizedLine, targetColumn, "exports"))
        {
            targetEndColumn = targetColumn + "exports".Length;
            return true;
        }

        const string moduleExportsTarget = "module.exports";
        if (!sanitizedLine.AsSpan(targetColumn).StartsWith(moduleExportsTarget, StringComparison.Ordinal))
            return false;

        var endColumn = targetColumn + moduleExportsTarget.Length;
        if (endColumn < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[endColumn]))
            return false;

        targetEndColumn = endColumn;
        return true;
    }

    private static bool TryReadJavaScriptTypeScriptQuotedExportPropertyName(
        string rawLine,
        string sanitizedLine,
        int quoteColumn,
        out string propertyName,
        out int propertyStartColumn,
        out int propertyEndColumn)
    {
        propertyName = string.Empty;
        propertyStartColumn = -1;
        propertyEndColumn = -1;

        if (quoteColumn < 0
            || quoteColumn >= sanitizedLine.Length
            || quoteColumn >= rawLine.Length
            || sanitizedLine[quoteColumn] is not ('\'' or '"'))
        {
            return false;
        }

        var probe = quoteColumn;
        if (!TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref probe, out _))
            return false;

        var rawEndColumn = Math.Min(probe, rawLine.Length);
        var rawName = rawLine[quoteColumn..rawEndColumn].Trim();
        if (rawName.Length < 2
            || rawName[0] != rawName[^1]
            || rawName[0] is not ('\'' or '"'))
        {
            return false;
        }

        propertyName = NormalizeJavaScriptTypeScriptExportedSpecifierName(rawName);
        propertyStartColumn = quoteColumn + 1;
        propertyEndColumn = probe - 1;
        return propertyName.Length > 0;
    }

    private static bool TryReadJavaScriptTypeScriptNumericExportPropertyName(
        string rawLine,
        string sanitizedLine,
        int numberColumn,
        out string propertyName,
        out int propertyStartColumn,
        out int propertyEndColumn)
    {
        propertyName = string.Empty;
        propertyStartColumn = -1;
        propertyEndColumn = -1;

        if (numberColumn < 0
            || numberColumn >= sanitizedLine.Length
            || numberColumn >= rawLine.Length
            || !char.IsDigit(sanitizedLine[numberColumn]))
        {
            return false;
        }

        var probe = numberColumn;
        if (!TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
            return false;

        var rawEndColumn = Math.Min(probe, rawLine.Length);
        propertyName = rawLine[numberColumn..rawEndColumn].Trim();
        propertyStartColumn = numberColumn;
        propertyEndColumn = probe - 1;
        return propertyName.Length > 0;
    }

    private static void ExtractJavaScriptTypeScriptCommonJsDefinePropertiesExports(
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
                if (!TryReadJavaScriptTypeScriptCommonJsDefinePropertiesExportObject(
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        lang,
                        out var objectColumn,
                        out var descriptorObjectLineIndex,
                        out var descriptorObjectColumn,
                        out var endLineIndex,
                        out var endColumn,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, objectColumn, sanitizedLine, includeBlockScope: false)
                    || IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, objectColumn, sanitizedLine))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                ExtractJavaScriptTypeScriptCommonJsExportObjectLiteralKeys(
                    fileId,
                    lang,
                    rawLines,
                    sanitizedLines,
                    symbols,
                    descriptorObjectLineIndex,
                    descriptorObjectColumn,
                    signature);

                if (endLineIndex > i)
                    i = endLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], endColumn + 1);
                sanitizedLine = sanitizedLines[i];
            }
        }
    }

    private static bool TryReadJavaScriptTypeScriptCommonJsDefinePropertiesExportObject(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        string lang,
        out int objectColumn,
        out int descriptorObjectLineIndex,
        out int descriptorObjectColumn,
        out int endLineIndex,
        out int endColumn,
        out string signature)
    {
        objectColumn = -1;
        descriptorObjectLineIndex = -1;
        descriptorObjectColumn = -1;
        endLineIndex = -1;
        endColumn = -1;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        objectColumn = SkipWhitespace(startLine, startColumn);
        const string definePropertiesCall = "Object.defineProperties";
        if (!startLine.AsSpan(objectColumn).StartsWith(definePropertiesCall, StringComparison.Ordinal))
            return false;

        var afterCallColumn = objectColumn + definePropertiesCall.Length;
        if (afterCallColumn < startLine.Length && IsJavaScriptTypeScriptIdentifierPart(startLine[afterCallColumn]))
            return false;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 48);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterCallColumn,
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
                out var targetLineIndex,
                out var targetColumn)
            || !TryReadJavaScriptTypeScriptCommonJsDefinePropertyTarget(
                sanitizedLines[targetLineIndex],
                targetColumn,
                out var targetEndColumn))
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                targetLineIndex,
                targetEndColumn,
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
                out descriptorObjectLineIndex,
                out descriptorObjectColumn)
            || sanitizedLines[descriptorObjectLineIndex][descriptorObjectColumn] != '{')
        {
            return false;
        }

        var (_, bodyStartLine, bodyEndLine) = ResolveRange(rawLines, descriptorObjectLineIndex, BodyStyle.Brace, lang, descriptorObjectColumn);
        if (bodyStartLine == null || bodyEndLine == null)
            return false;

        if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                sanitizedLines,
                openParenLineIndex,
                openParenColumn,
                scanEndExclusive,
                out endLineIndex,
                out endColumn))
        {
            return false;
        }

        signature = BuildJavaScriptTypeScriptStatementSignature(rawLines, startLineIndex, objectColumn, endLineIndex, endColumn);
        return true;
    }

    private static void ExtractJavaScriptTypeScriptCommonJsObjectAssignExports(
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
                if (!TryReadJavaScriptTypeScriptCommonJsObjectAssignExportObject(
                        rawLines,
                        sanitizedLines,
                        i,
                        statementStart,
                        lang,
                        out var objectColumn,
                        out var sourceObjectLineIndex,
                        out var sourceObjectColumn,
                        out var endLineIndex,
                        out var endColumn,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, objectColumn, sanitizedLine, includeBlockScope: false)
                    || IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, i, objectColumn, sanitizedLine))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                ExtractJavaScriptTypeScriptCommonJsExportObjectLiteralKeys(
                    fileId,
                    lang,
                    rawLines,
                    sanitizedLines,
                    symbols,
                    sourceObjectLineIndex,
                    sourceObjectColumn,
                    signature);

                if (endLineIndex > i)
                    i = endLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], endColumn + 1);
                sanitizedLine = sanitizedLines[i];
            }
        }
    }

    private static bool TryReadJavaScriptTypeScriptCommonJsObjectAssignExportObject(
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int startColumn,
        string lang,
        out int objectColumn,
        out int sourceObjectLineIndex,
        out int sourceObjectColumn,
        out int endLineIndex,
        out int endColumn,
        out string signature)
    {
        objectColumn = -1;
        sourceObjectLineIndex = -1;
        sourceObjectColumn = -1;
        endLineIndex = -1;
        endColumn = -1;
        signature = string.Empty;

        var startLine = sanitizedLines[startLineIndex];
        objectColumn = SkipWhitespace(startLine, startColumn);
        const string objectAssignCall = "Object.assign";
        if (!startLine.AsSpan(objectColumn).StartsWith(objectAssignCall, StringComparison.Ordinal))
            return false;

        var afterCallColumn = objectColumn + objectAssignCall.Length;
        if (afterCallColumn < startLine.Length && IsJavaScriptTypeScriptIdentifierPart(startLine[afterCallColumn]))
            return false;

        var scanEndExclusive = Math.Min(sanitizedLines.Length, startLineIndex + 48);
        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                startLineIndex,
                afterCallColumn,
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
                out var targetLineIndex,
                out var targetColumn)
            || !TryReadJavaScriptTypeScriptCommonJsDefinePropertyTarget(
                sanitizedLines[targetLineIndex],
                targetColumn,
                out var targetEndColumn))
        {
            return false;
        }

        if (!TryFindNextJavaScriptTypeScriptNonWhitespace(
                sanitizedLines,
                targetLineIndex,
                targetEndColumn,
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
                out sourceObjectLineIndex,
                out sourceObjectColumn)
            || sanitizedLines[sourceObjectLineIndex][sourceObjectColumn] != '{')
        {
            return false;
        }

        var (_, bodyStartLine, bodyEndLine) = ResolveRange(rawLines, sourceObjectLineIndex, BodyStyle.Brace, lang, sourceObjectColumn);
        if (bodyStartLine == null || bodyEndLine == null)
            return false;

        if (!TryFindJavaScriptTypeScriptDynamicImportCloseParen(
                sanitizedLines,
                openParenLineIndex,
                openParenColumn,
                scanEndExclusive,
                out endLineIndex,
                out endColumn))
        {
            return false;
        }

        signature = BuildJavaScriptTypeScriptStatementSignature(rawLines, startLineIndex, objectColumn, endLineIndex, endColumn);
        return true;
    }

    private static void ExtractJavaScriptTypeScriptCommonJsExportObjectLiteralKeys(
        long fileId,
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        int descriptorObjectLineIndex,
        int descriptorObjectColumn,
        string signature)
    {
        var (_, bodyStartLine, bodyEndLine) = ResolveRange(rawLines, descriptorObjectLineIndex, BodyStyle.Brace, lang, descriptorObjectColumn);
        if (bodyStartLine == null || bodyEndLine == null)
            return;

        var target = CreateJavaScriptClassScanTarget(
            rawLines,
            lang,
            descriptorObjectLineIndex,
            descriptorObjectColumn,
            bodyStartLine,
            bodyEndLine,
            "object",
            "exports",
            isExported: true);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var braceDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var skippingPropertyValue = false;

        for (int lineIndex = target.ScanStartIndex; lineIndex < target.ScanEndExclusive; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var scanColumn = lineIndex == target.ScanStartIndex
                ? target.FirstLineScanOffset
                : 0;

            while (scanColumn < sanitizedLine.Length)
            {
                var ch = sanitizedLine[scanColumn];
                if (skippingPropertyValue)
                {
                    if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                    {
                        if (ch == ',')
                        {
                            skippingPropertyValue = false;
                            scanColumn++;
                            continue;
                        }

                        if (ch == '}')
                        {
                            skippingPropertyValue = false;
                            continue;
                        }
                    }

                    switch (ch)
                    {
                        case '{':
                            braceDepth++;
                            break;
                        case '}':
                            if (braceDepth > 0)
                                braceDepth--;
                            break;
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
                    }

                    scanColumn++;
                    continue;
                }

                if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                {
                    while (scanColumn < sanitizedLine.Length
                        && (char.IsWhiteSpace(sanitizedLine[scanColumn]) || sanitizedLine[scanColumn] is ',' or ';'))
                    {
                        scanColumn++;
                    }

                    if (scanColumn >= sanitizedLine.Length)
                        break;

                    var remainingLine = sanitizedLine[scanColumn..];
                    if (remainingLine.StartsWith("...", StringComparison.Ordinal))
                    {
                        scanColumn += 3;
                        skippingPropertyValue = true;
                        continue;
                    }

                    var propertyMatch = JavaScriptTypeScriptExportedObjectLiteralPropertyRegex.Match(remainingLine);
                    if (propertyMatch.Success)
                    {
                        var propertyName = propertyMatch.Groups["name"].Value;
                        AddJavaScriptTypeScriptCommonJsExportObjectLiteralSymbol(
                            fileId,
                            rawLines,
                            symbols,
                            seenNames,
                            propertyName,
                            lineIndex,
                            scanColumn + propertyMatch.Index,
                            signature);
                        scanColumn += propertyMatch.Length;
                        skippingPropertyValue = true;
                        continue;
                    }

                    if (TryReadJavaScriptTypeScriptLiteralObjectLiteralKeyName(
                            sanitizedLine,
                            rawLines[lineIndex],
                            scanColumn,
                            out var literalPropertyName,
                            out var literalValueStartColumn))
                    {
                        AddJavaScriptTypeScriptCommonJsExportObjectLiteralSymbol(
                            fileId,
                            rawLines,
                            symbols,
                            seenNames,
                            literalPropertyName,
                            lineIndex,
                            scanColumn,
                            signature);
                        scanColumn = literalValueStartColumn;
                        skippingPropertyValue = true;
                        continue;
                    }

                    if (TryReadJavaScriptTypeScriptComputedLiteralObjectLiteralKeyName(
                            sanitizedLine,
                            rawLines[lineIndex],
                            scanColumn,
                            out var computedLiteralPropertyName,
                            out var computedLiteralValueStartColumn))
                    {
                        AddJavaScriptTypeScriptCommonJsExportObjectLiteralSymbol(
                            fileId,
                            rawLines,
                            symbols,
                            seenNames,
                            computedLiteralPropertyName,
                            lineIndex,
                            scanColumn,
                            signature);
                        scanColumn = computedLiteralValueStartColumn;
                        skippingPropertyValue = true;
                        continue;
                    }

                    if (TrySkipJavaScriptTypeScriptNonIdentifierObjectLiteralKey(sanitizedLine, ref scanColumn))
                    {
                        skippingPropertyValue = true;
                        continue;
                    }

                    var shorthandMatch = JavaScriptTypeScriptExportedObjectLiteralShorthandPropertyRegex.Match(remainingLine);
                    if (shorthandMatch.Success)
                    {
                        var propertyName = shorthandMatch.Groups["name"].Value;
                        AddJavaScriptTypeScriptCommonJsExportObjectLiteralSymbol(
                            fileId,
                            rawLines,
                            symbols,
                            seenNames,
                            propertyName,
                            lineIndex,
                            scanColumn + shorthandMatch.Index,
                            signature);
                        scanColumn += shorthandMatch.Length;
                        continue;
                    }
                }

                switch (ch)
                {
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
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
                }

                scanColumn++;
            }
        }
    }

    private static void AddJavaScriptTypeScriptCommonJsExportObjectLiteralSymbol(
        long fileId,
        string[] rawLines,
        List<SymbolRecord> symbols,
        HashSet<string> seenNames,
        string propertyName,
        int lineIndex,
        int startColumn,
        string signature)
    {
        if (propertyName.Length == 0
            || propertyName == "__esModule"
            || !seenNames.Add(propertyName))
        {
            return;
        }

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = propertyName,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = signature,
                Visibility = "export",
            },
            rawLines[lineIndex]);
    }

    private static void ExtractJavaScriptTypeScriptExportedObjectLiteralProperties(
        long fileId,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> objectLiteralTargets)
    {
        foreach (var target in objectLiteralTargets.Where(t => t.IsExported))
        {
            var braceDepth = 0;
            var parenDepth = 0;
            var bracketDepth = 0;
            var skippingPropertyValue = false;

            for (int lineIndex = target.ScanStartIndex; lineIndex < target.ScanEndExclusive; lineIndex++)
            {
                var sanitizedLine = sanitizedLines[lineIndex];
                var scanColumn = lineIndex == target.ScanStartIndex
                    ? target.FirstLineScanOffset
                    : 0;

                while (scanColumn < sanitizedLine.Length)
                {
                    var ch = sanitizedLine[scanColumn];
                    if (skippingPropertyValue)
                    {
                        if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                        {
                            if (ch == ',')
                            {
                                skippingPropertyValue = false;
                                scanColumn++;
                                continue;
                            }

                            if (ch == '}')
                            {
                                skippingPropertyValue = false;
                                continue;
                            }
                        }

                        switch (ch)
                        {
                            case '{':
                                braceDepth++;
                                break;
                            case '}':
                                if (braceDepth > 0)
                                    braceDepth--;
                                break;
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
                        }

                        scanColumn++;
                        continue;
                    }

                    if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0)
                    {
                        while (scanColumn < sanitizedLine.Length
                            && (char.IsWhiteSpace(sanitizedLine[scanColumn]) || sanitizedLine[scanColumn] is ',' or ';'))
                        {
                            scanColumn++;
                        }

                        if (scanColumn >= sanitizedLine.Length)
                            break;

                        var remainingLine = sanitizedLine[scanColumn..];
                        if (remainingLine.StartsWith("...", StringComparison.Ordinal))
                        {
                            scanColumn += 3;
                            skippingPropertyValue = true;
                            continue;
                        }

                        var propertyMatch = JavaScriptTypeScriptExportedObjectLiteralPropertyRegex.Match(remainingLine);
                        if (propertyMatch.Success)
                        {
                            var propertyName = propertyMatch.Groups["name"].Value;
                            var hasExistingContainerSymbol = symbols.Any(s =>
                                s.Name == propertyName
                                && s.ContainerKind == "object"
                                && s.ContainerName == target.ContainerName);
                            if (!hasExistingContainerSymbol)
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols: null,
                                    lineIndex + 1,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = "property",
                                        Name = propertyName,
                                        Line = lineIndex + 1,
                                        StartLine = lineIndex + 1,
                                        StartColumn = scanColumn + propertyMatch.Index,
                                        EndLine = lineIndex + 1,
                                        Signature = rawLines[lineIndex].Trim(),
                                        ContainerKind = "object",
                                        ContainerName = target.ContainerName,
                                        Visibility = "export",
                                    },
                                    rawLines[lineIndex]);
                            }

                            scanColumn += propertyMatch.Length;
                            skippingPropertyValue = true;
                            continue;
                        }

                        if (TryReadJavaScriptTypeScriptLiteralObjectLiteralKeyName(
                                sanitizedLine,
                                rawLines[lineIndex],
                                scanColumn,
                                out var literalPropertyName,
                                out var literalValueStartColumn))
                        {
                            var hasExistingContainerSymbol = symbols.Any(s =>
                                s.Name == literalPropertyName
                                && s.ContainerKind == "object"
                                && s.ContainerName == target.ContainerName);
                            if (!hasExistingContainerSymbol)
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols: null,
                                    lineIndex + 1,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = "property",
                                        Name = literalPropertyName,
                                        Line = lineIndex + 1,
                                        StartLine = lineIndex + 1,
                                        StartColumn = scanColumn,
                                        EndLine = lineIndex + 1,
                                        Signature = rawLines[lineIndex].Trim(),
                                        ContainerKind = "object",
                                        ContainerName = target.ContainerName,
                                        Visibility = "export",
                                    },
                                    rawLines[lineIndex]);
                            }

                            scanColumn = literalValueStartColumn;
                            skippingPropertyValue = true;
                            continue;
                        }

                        if (TryReadJavaScriptTypeScriptComputedLiteralObjectLiteralKeyName(
                                sanitizedLine,
                                rawLines[lineIndex],
                                scanColumn,
                                out var computedLiteralPropertyName,
                                out var computedLiteralValueStartColumn))
                        {
                            var hasExistingContainerSymbol = symbols.Any(s =>
                                s.Name == computedLiteralPropertyName
                                && s.ContainerKind == "object"
                                && s.ContainerName == target.ContainerName);
                            if (!hasExistingContainerSymbol)
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols: null,
                                    lineIndex + 1,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = "property",
                                        Name = computedLiteralPropertyName,
                                        Line = lineIndex + 1,
                                        StartLine = lineIndex + 1,
                                        StartColumn = scanColumn,
                                        EndLine = lineIndex + 1,
                                        Signature = rawLines[lineIndex].Trim(),
                                        ContainerKind = "object",
                                        ContainerName = target.ContainerName,
                                        Visibility = "export",
                                    },
                                    rawLines[lineIndex]);
                            }

                            scanColumn = computedLiteralValueStartColumn;
                            skippingPropertyValue = true;
                            continue;
                        }

                        if (TrySkipJavaScriptTypeScriptNonIdentifierObjectLiteralKey(sanitizedLine, ref scanColumn))
                        {
                            skippingPropertyValue = true;
                            continue;
                        }

                        var shorthandMatch = JavaScriptTypeScriptExportedObjectLiteralShorthandPropertyRegex.Match(remainingLine);
                        if (shorthandMatch.Success)
                        {
                            var propertyName = shorthandMatch.Groups["name"].Value;
                            var hasExistingContainerSymbol = symbols.Any(s =>
                                s.Name == propertyName
                                && s.ContainerKind == "object"
                                && s.ContainerName == target.ContainerName);
                            if (!hasExistingContainerSymbol)
                            {
                                AddSymbolRecord(
                                    symbols,
                                    cssSeenSymbols: null,
                                    lineIndex + 1,
                                    new SymbolRecord
                                    {
                                        FileId = fileId,
                                        Kind = "property",
                                        Name = propertyName,
                                        Line = lineIndex + 1,
                                        StartLine = lineIndex + 1,
                                        StartColumn = scanColumn + shorthandMatch.Index,
                                        EndLine = lineIndex + 1,
                                        Signature = rawLines[lineIndex].Trim(),
                                        ContainerKind = "object",
                                        ContainerName = target.ContainerName,
                                        Visibility = "export",
                                    },
                                    rawLines[lineIndex]);
                            }

                            scanColumn += shorthandMatch.Length;
                            continue;
                        }
                    }

                    switch (ch)
                    {
                        case '{':
                            braceDepth++;
                            break;
                        case '}':
                            if (braceDepth > 0)
                                braceDepth--;
                            break;
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
                    }

                    scanColumn++;
                }
            }
        }
    }

    private static bool TryReadJavaScriptTypeScriptLiteralObjectLiteralKeyName(
        string sanitizedLine,
        string rawLine,
        int startColumn,
        out string propertyName,
        out int valueStartColumn)
    {
        propertyName = string.Empty;
        valueStartColumn = startColumn;

        if (startColumn < 0
            || startColumn >= sanitizedLine.Length
            || startColumn >= rawLine.Length)
        {
            return false;
        }

        var probe = startColumn;
        var keyStartColumn = startColumn;
        if (sanitizedLine[startColumn] is '\'' or '"')
        {
            if (!TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref probe, out _))
                return false;

            var rawEndColumn = Math.Min(probe, rawLine.Length);
            var rawKey = rawLine[keyStartColumn..rawEndColumn].Trim();
            if (rawKey.Length < 2
                || rawKey[0] != rawKey[^1]
                || rawKey[0] is not ('\'' or '"'))
            {
                return false;
            }

            propertyName = rawKey[1..^1];
        }
        else if (char.IsDigit(sanitizedLine[startColumn]))
        {
            if (!TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
                return false;

            var rawEndColumn = Math.Min(probe, rawLine.Length);
            propertyName = rawLine[keyStartColumn..rawEndColumn].Trim();
        }
        else
        {
            return false;
        }

        if (propertyName.Length == 0)
            return false;

        while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
            probe++;

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ':')
        {
            propertyName = string.Empty;
            return false;
        }

        valueStartColumn = probe + 1;
        return true;
    }

    private static bool TryReadJavaScriptTypeScriptComputedLiteralObjectLiteralKeyName(
        string sanitizedLine,
        string rawLine,
        int startColumn,
        out string propertyName,
        out int valueStartColumn)
    {
        propertyName = string.Empty;
        valueStartColumn = startColumn;

        if (startColumn < 0
            || startColumn >= sanitizedLine.Length
            || startColumn >= rawLine.Length
            || sanitizedLine[startColumn] != '[')
        {
            return false;
        }

        var probe = startColumn + 1;
        while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
            probe++;

        if (probe >= sanitizedLine.Length || probe >= rawLine.Length)
            return false;

        if (sanitizedLine[probe] is '\'' or '"')
        {
            var keyStartColumn = probe;
            if (!TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref probe, out _))
                return false;

            var rawEndColumn = Math.Min(probe, rawLine.Length);
            var rawKey = rawLine[keyStartColumn..rawEndColumn].Trim();
            if (rawKey.Length < 2
                || rawKey[0] != rawKey[^1]
                || rawKey[0] is not ('\'' or '"'))
            {
                return false;
            }

            propertyName = rawKey[1..^1];
        }
        else if (char.IsDigit(sanitizedLine[probe]))
        {
            var keyStartColumn = probe;
            if (!TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
                return false;

            var rawEndColumn = Math.Min(probe, rawLine.Length);
            propertyName = rawLine[keyStartColumn..rawEndColumn].Trim();
        }
        else
        {
            return false;
        }

        if (propertyName.Length == 0)
            return false;

        while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
            probe++;

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ']')
        {
            propertyName = string.Empty;
            return false;
        }

        probe++;
        while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
            probe++;

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ':')
        {
            propertyName = string.Empty;
            return false;
        }

        valueStartColumn = probe + 1;
        return true;
    }

    private static bool StartsJavaScriptTypeScriptFunctionAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        while (rhs.Length > 0)
        {
            if (IsJavaScriptTypeScriptKeywordAt(rhs, 0, "function")
                || StartsJavaScriptTypeScriptAsyncFunctionAssignmentValue(rhs)
                || StartsJavaScriptTypeScriptGenericArrowAssignmentValue(rhs)
                || JavaScriptTypeScriptArrowAssignmentValueRegex.IsMatch(rhs))
            {
                return true;
            }

            if (rhs[0] != '(')
                return false;

            rhs = rhs[1..].TrimStart();
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptArrowFunctionAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        while (rhs.Length > 0)
        {
            if (StartsJavaScriptTypeScriptGenericArrowAssignmentValue(rhs)
                || JavaScriptTypeScriptArrowAssignmentValueRegex.IsMatch(rhs))
            {
                return true;
            }

            if (rhs[0] != '(')
                return false;

            rhs = rhs[1..].TrimStart();
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptClassAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        while (rhs.Length > 0)
        {
            if (IsJavaScriptTypeScriptKeywordAt(rhs, 0, "class"))
                return true;

            if (rhs[0] != '(')
                return false;

            rhs = rhs[1..].TrimStart();
        }

        return false;
    }

    private static bool StartsJavaScriptTypeScriptAsyncFunctionAssignmentValue(string rhs)
    {
        if (!IsJavaScriptTypeScriptKeywordAt(rhs, 0, "async"))
            return false;

        var asyncRemainder = rhs["async".Length..].TrimStart();
        return IsJavaScriptTypeScriptKeywordAt(asyncRemainder, 0, "function");
    }

    private static bool StartsJavaScriptTypeScriptPotentialGenericArrowAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        if (IsJavaScriptTypeScriptKeywordAt(rhs, 0, "async"))
            rhs = rhs["async".Length..].TrimStart();

        return rhs.Length > 0 && rhs[0] == '<';
    }

    private static string CollectJavaScriptTypeScriptAssignedRhsHeader(string[] sanitizedLines, int startLineIndex, int startColumn)
    {
        var builder = new System.Text.StringBuilder(EstimateJavaScriptTypeScriptStatementCapacity(sanitizedLines, startLineIndex));
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var genericDepth = 0;
        var sawGenericStart = false;

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex
                ? Math.Max(0, startColumn)
                : 0;
            if (column >= sanitizedLine.Length)
                continue;

            if (builder.Length > 0)
                builder.Append(' ');

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                builder.Append(ch);

                if (!sawGenericStart)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;

                    if (ch == '<')
                    {
                        sawGenericStart = true;
                        genericDepth = 1;
                    }

                    continue;
                }

                switch (ch)
                {
                    case '<':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            genericDepth++;
                        break;
                    case '>':
                        if (parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0
                            && genericDepth > 0
                            && (column == 0 || sanitizedLine[column - 1] != '='))
                        {
                            genericDepth--;
                        }
                        break;
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
                        if (genericDepth == 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                            return builder.ToString().Trim();

                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                    case '=':
                        if (column + 1 < sanitizedLine.Length
                            && sanitizedLine[column + 1] == '>'
                            && genericDepth == 0
                            && parenDepth == 0
                            && bracketDepth == 0
                            && braceDepth == 0)
                        {
                            builder.Append('>');
                            column++;
                            return builder.ToString().Trim();
                        }
                        break;
                }
            }

            if (sawGenericStart
                && genericDepth == 0
                && parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool StartsJavaScriptTypeScriptGenericArrowAssignmentValue(string rhs)
    {
        rhs = rhs.TrimStart();
        if (IsJavaScriptTypeScriptKeywordAt(rhs, 0, "async"))
            rhs = rhs["async".Length..].TrimStart();

        if (rhs.Length == 0 || rhs[0] != '<')
            return false;

        var genericEnd = FindJavaScriptTypeScriptBalancedGenericListEnd(rhs, 0);
        if (genericEnd < 0)
            return false;

        var remainder = rhs[(genericEnd + 1)..].TrimStart();
        if (remainder.Length == 0)
            return false;

        if (remainder[0] == '(')
        {
            var parameterListEnd = FindJavaScriptTypeScriptBalancedDelimiterEnd(remainder, 0, '(', ')');
            if (parameterListEnd < 0)
                return false;

            remainder = remainder[(parameterListEnd + 1)..].TrimStart();
        }
        else
        {
            var parameterNameLength = ReadJavaScriptTypeScriptIdentifierLength(remainder, 0);
            if (parameterNameLength <= 0)
                return false;

            remainder = remainder[parameterNameLength..].TrimStart();
        }

        return remainder.StartsWith("=>", StringComparison.Ordinal);
    }

    private static bool TryCollectJavaScriptTypeScriptAssignedRhs(
        string[] rawLines,
        string[] sanitizedLines,
        int assignmentLineIndex,
        int assignmentStartColumn,
        int sameLineRhsColumn,
        string lang,
        out string rhs,
        out int rhsStartLineIndex,
        out int rhsStartColumn,
        out int rhsEndLineIndex,
        out int rhsEndColumn,
        out string signature)
    {
        rhs = string.Empty;
        rhsStartLineIndex = assignmentLineIndex;
        rhsStartColumn = sameLineRhsColumn;
        rhsEndLineIndex = assignmentLineIndex;
        rhsEndColumn = -1;
        signature = string.Empty;

        var builderCapacity = EstimateJavaScriptTypeScriptStatementCapacity(sanitizedLines, assignmentLineIndex);
        var rhsBuilder = new System.Text.StringBuilder(builderCapacity);
        var signatureBuilder = new System.Text.StringBuilder(builderCapacity);
        var pendingWrapperParenClose = false;

        for (int lineIndex = assignmentLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var column = lineIndex == assignmentLineIndex
                ? Math.Max(0, sameLineRhsColumn)
                : 0;

            if (!TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(sanitizedLines, ref lineIndex, ref column))
                continue;

            var sanitizedLine = sanitizedLines[lineIndex];
            while (sanitizedLines[lineIndex][column] == '('
                && HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(sanitizedLines[lineIndex], column))
            {
                column++;
                pendingWrapperParenClose = true;
                if (!TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(sanitizedLines, ref lineIndex, ref column))
                    return false;

                sanitizedLine = sanitizedLines[lineIndex];
            }

            if (pendingWrapperParenClose && column < sanitizedLine.Length && sanitizedLine[column] == ')')
            {
                column++;
                pendingWrapperParenClose = false;
            }

            var statementEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, column, lang);
            var sliceEndExclusive = statementEndColumn >= column
                ? statementEndColumn + 1
                : sanitizedLine.Length;

            var rhsStartSliceColumn = Math.Min(column, sanitizedLine.Length);
            var statementSliceEndColumn = Math.Min(sliceEndExclusive, sanitizedLine.Length);
            var rhsSlice = rhsStartSliceColumn < statementSliceEndColumn
                ? sanitizedLine[rhsStartSliceColumn..statementSliceEndColumn].TrimEnd()
                : string.Empty;
            if (rhsSlice.Length > 0)
            {
                if (rhsBuilder.Length == 0)
                {
                    rhsStartLineIndex = lineIndex;
                    rhsStartColumn = rhsStartSliceColumn;
                }

                if (rhsBuilder.Length > 0)
                    rhsBuilder.Append(' ');
                rhsBuilder.Append(rhsSlice);
            }

            var signatureSlice = lineIndex == assignmentLineIndex
                ? rawLines[lineIndex][Math.Min(assignmentStartColumn, rawLines[lineIndex].Length)..Math.Min(rawLines[lineIndex].Length, statementSliceEndColumn)].Trim()
                : rawLines[lineIndex].Trim();
            if (signatureSlice.Length > 0)
            {
                if (signatureBuilder.Length > 0)
                    signatureBuilder.Append(' ');
                signatureBuilder.Append(signatureSlice);
            }

            if (statementEndColumn >= column)
            {
                rhsEndLineIndex = lineIndex;
                rhsEndColumn = statementEndColumn;
                rhs = rhsBuilder.ToString().Trim();
                signature = signatureBuilder.ToString().Trim();
                return true;
            }
        }

        if (rhsBuilder.Length > 0)
        {
            rhs = rhsBuilder.ToString().Trim();
            signature = signatureBuilder.ToString().Trim();
            rhsEndLineIndex = Math.Max(assignmentLineIndex, sanitizedLines.Length - 1);
            rhsEndColumn = sanitizedLines[rhsEndLineIndex].Length - 1;
            return true;
        }

        rhs = string.Empty;
        signature = string.Empty;
        return false;
    }

    private static bool TryAdvanceJavaScriptTypeScriptAssignedRhsCursor(string[] sanitizedLines, ref int lineIndex, ref int column)
    {
        while (lineIndex < sanitizedLines.Length)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                column++;

            if (column < sanitizedLine.Length)
                return true;

            lineIndex++;
            column = 0;
        }

        return false;
    }

    private static bool TryFindJavaScriptTypeScriptAssignedRhsStart(
        string[] sanitizedLines,
        int assignmentLineIndex,
        int sameLineRhsColumn,
        out int startLineIndex,
        out int startColumn)
    {
        for (int lineIndex = assignmentLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == assignmentLineIndex
                ? Math.Max(0, sameLineRhsColumn)
                : 0;

            while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                column++;

            if (column >= sanitizedLine.Length)
                continue;

            if (sanitizedLine[column] == '('
                && HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(sanitizedLine, column))
            {
                continue;
            }

            if (sanitizedLine[column] == ')')
            {
                var remainder = sanitizedLine[column..].Trim();
                if (remainder.Length == 0 || remainder == ")" || remainder == ");")
                    continue;
            }

            startLineIndex = lineIndex;
            startColumn = column;
            return true;
        }

        startLineIndex = assignmentLineIndex;
        startColumn = sameLineRhsColumn;
        return false;
    }

    private static bool TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
        string[] rawLines,
        int startLineIndex,
        int startColumn,
        string? lang,
        out int openBraceLineIndex,
        out int openBraceColumn)
    {
        openBraceLineIndex = -1;
        openBraceColumn = -1;

        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var awaitingFunctionBody = false;
        var awaitingArrowBody = false;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var lexState = new JavaScriptLexState();

        for (int lineIndex = startLineIndex; lineIndex < rawLines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(rawLines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var column = lineIndex == startLineIndex
                ? Math.Max(0, startColumn)
                : 0;

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                var wasFunctionHeaderActive = functionHeaderState.Active;

                if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                        tokenEnd++;

                    if (sanitizedLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                    ref functionHeaderState,
                    sanitizedLine,
                    column,
                    lang ?? "javascript",
                    out var functionHeaderAdvanceColumns);
                if (wasFunctionHeaderActive && !functionHeaderState.Active)
                    awaitingFunctionBody = true;

                if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                {
                    column += functionHeaderAdvanceColumns;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                    continue;

                if (awaitingFunctionBody)
                {
                    if (ch == '{')
                    {
                        openBraceLineIndex = lineIndex;
                        openBraceColumn = column;
                        return true;
                    }

                    return false;
                }

                if (awaitingArrowBody)
                {
                    if (ch == '{')
                    {
                        openBraceLineIndex = lineIndex;
                        openBraceColumn = column;
                        return true;
                    }

                    return false;
                }

                if (ch == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    continue;
                }

                if (lang == "typescript" && ch == '<' && parenDepth == 0 && bracketDepth == 0)
                {
                    angleDepth++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0 && (column == 0 || sanitizedLine[column - 1] != '='))
                {
                    angleDepth--;
                    continue;
                }

                if (ch == '='
                    && column + 1 < sanitizedLine.Length
                    && sanitizedLine[column + 1] == '>'
                    && parenDepth == 0
                    && bracketDepth == 0
                    && angleDepth == 0)
                {
                    awaitingArrowBody = true;
                    column++;
                }
            }
        }

        return false;
    }

    private static bool HasOnlyJavaScriptTypeScriptAssignedRhsWrapperParensToLineEnd(string sanitizedLine, int startColumn)
    {
        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (char.IsWhiteSpace(ch) || ch == '(')
                continue;

            return false;
        }

        return true;
    }

    private static bool TrySkipJavaScriptTypeScriptNonIdentifierObjectLiteralKey(string sanitizedLine, ref int index)
    {
        var probe = index;
        if (TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref probe, out _)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
        {
            while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
                probe++;

            if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ':')
                return false;

            index = probe + 1;
            return true;
        }

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != '[')
            return false;

        var bracketDepth = 1;
        probe++;
        while (probe < sanitizedLine.Length && bracketDepth > 0)
        {
            if (sanitizedLine[probe] == '[')
            {
                bracketDepth++;
            }
            else if (sanitizedLine[probe] == ']')
            {
                bracketDepth--;
            }

            probe++;
        }

        if (bracketDepth != 0)
            return false;

        while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
            probe++;

        if (probe >= sanitizedLine.Length || sanitizedLine[probe] != ':')
            return false;

        index = probe + 1;
        return true;
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

    private static bool IsJavaScriptTypeScriptKeywordAt(string text, int index, string keyword)
    {
        if (index < 0
            || index + keyword.Length > text.Length
            || !text.AsSpan(index, keyword.Length).SequenceEqual(keyword.AsSpan()))
        {
            return false;
        }

        var before = index > 0 ? text[index - 1] : '\0';
        if (char.IsLetterOrDigit(before) || before is '_' or '$')
            return false;

        var afterIndex = index + keyword.Length;
        if (afterIndex >= text.Length)
            return true;

        var after = text[afterIndex];
        return !(char.IsLetterOrDigit(after) || after is '_' or '$');
    }

    private static int FindJavaScriptTypeScriptKeywordIndex(string text, string keyword)
    {
        for (int index = 0; index <= text.Length - keyword.Length; index++)
        {
            if (IsJavaScriptTypeScriptKeywordAt(text, index, keyword))
                return index;
        }

        return -1;
    }

    private static bool ContainsJavaScriptTypeScriptKeyword(string text, string keyword)
    {
        return FindJavaScriptTypeScriptKeywordIndex(text, keyword) >= 0;
    }

    private static bool HasPendingJavaScriptTypeScriptImportAttributes(string clause)
    {
        var withIndex = FindJavaScriptTypeScriptKeywordIndex(clause, "with");
        var assertIndex = FindJavaScriptTypeScriptKeywordIndex(clause, "assert");
        var attributeIndex = withIndex >= 0 && assertIndex >= 0
            ? Math.Min(withIndex, assertIndex)
            : Math.Max(withIndex, assertIndex);
        if (attributeIndex < 0)
            return false;

        var braceDepth = 0;
        var sawOpeningBrace = false;
        for (int index = attributeIndex; index < clause.Length; index++)
        {
            var ch = clause[index];
            if (ch == '{')
            {
                braceDepth++;
                sawOpeningBrace = true;
            }
            else if (ch == '}' && braceDepth > 0)
            {
                braceDepth--;
            }
        }

        return !sawOpeningBrace || braceDepth > 0;
    }

    private static string SkipJavaScriptTypeScriptTypeOnlyExportModifier(string exportRemainder)
    {
        if (IsJavaScriptTypeScriptKeywordAt(exportRemainder, 0, "type"))
            return exportRemainder["type".Length..].TrimStart();

        return exportRemainder;
    }

    private static int FindJavaScriptTypeScriptBalancedGenericListEnd(string text, int startIndex)
    {
        if (startIndex < 0
            || startIndex >= text.Length
            || text[startIndex] != '<')
        {
            return -1;
        }

        var depth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (int index = startIndex; index < text.Length; index++)
        {
            var ch = text[index];
            switch (ch)
            {
                case '<':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        depth++;
                    break;
                case '>':
                    if (parenDepth == 0
                        && bracketDepth == 0
                        && braceDepth == 0
                        && depth > 0
                        && (index == 0 || text[index - 1] != '='))
                    {
                        depth--;
                        if (depth == 0)
                            return index;
                    }
                    break;
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
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptBalancedDelimiterEnd(string text, int startIndex, char openChar, char closeChar)
    {
        if (startIndex < 0
            || startIndex >= text.Length
            || text[startIndex] != openChar)
        {
            return -1;
        }

        var depth = 0;
        for (int index = startIndex; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == openChar)
            {
                depth++;
            }
            else if (ch == closeChar)
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return -1;
    }

    private static int ReadJavaScriptTypeScriptIdentifierLength(string text, int startIndex)
    {
        if (startIndex < 0 || startIndex >= text.Length)
            return 0;

        var first = text[startIndex];
        if (!(char.IsLetter(first) || first is '_' or '$'))
            return 0;

        var index = startIndex + 1;
        while (index < text.Length)
        {
            var ch = text[index];
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '$'))
                break;

            index++;
        }

        return index - startIndex;
    }

    private static IEnumerable<string> ParseJavaScriptTypeScriptReExportedNames(string specifierList)
    {
        foreach (var rawSpecifier in SplitJavaScriptTypeScriptExportSpecifiers(specifierList))
        {
            var specifier = rawSpecifier.Trim();
            if (specifier.Length == 0)
                continue;

            if (specifier.StartsWith("type ", StringComparison.Ordinal))
                specifier = specifier["type ".Length..].TrimStart();

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
        exportedName = exportedName.Trim();
        if (exportedName.Length < 2 || exportedName[0] is not ('\'' or '"'))
            return exportedName;

        var quote = exportedName[0];
        if (exportedName[^1] != quote)
            return exportedName;

        var builder = new StringBuilder(Math.Max(0, exportedName.Length - 2));
        for (var index = 1; index < exportedName.Length - 1; index++)
        {
            var ch = exportedName[index];
            if (ch == '\\' && index + 1 < exportedName.Length - 1)
            {
                builder.Append(exportedName[index + 1]);
                index++;
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    // Scans forward from (`startLineIndex`, `startColumn`) through the lex-sanitized source for
    // the first `{`, hopping across lines when only whitespace (including newlines) remains. The
    // passed `sanitizedStartLine` is the already-sanitized version of lines[startLineIndex] and
    // `lineEndState` is the lexer state AFTER that line. Any non-whitespace, non-`{` character
    // aborts the scan (returns false) so we don't misclassify arbitrary RHS expressions as object
    // literals. Strings / comments stay masked because we drive the scan through LexJavaScriptLine.
    // (`startLineIndex`, `startColumn`) から lex sanitized のソースを前方に走査し、最初の `{` を探す。
    // 空白 (改行を含む) だけなら行を跨いで続行する。`sanitizedStartLine` は lines[startLineIndex] の
    // sanitized 版で、`lineEndState` はそのライン終了時の lexer state。`{` 以外の非空白文字が現れた時点で
    // 走査を打ち切る (false を返す) ので、オブジェクトリテラルでない右辺を誤って拾わない。
    // LexJavaScriptLine を介するため、文字列・コメントは常にマスクされた状態で判定できる。
    private static bool TryFindJavaScriptTypeScriptObjectLiteralOpenBrace(
        string[] lines,
        int startLineIndex,
        int startColumn,
        string sanitizedStartLine,
        JavaScriptLexState lineEndState,
        out int openBraceLineIndex,
        out int openBraceColumn)
    {
        openBraceLineIndex = -1;
        openBraceColumn = -1;

        for (int c = Math.Max(0, startColumn); c < sanitizedStartLine.Length; c++)
        {
            var ch = sanitizedStartLine[c];
            if (char.IsWhiteSpace(ch))
                continue;
            if (ch == '{')
            {
                openBraceLineIndex = startLineIndex;
                openBraceColumn = c;
                return true;
            }
            return false;
        }

        var lexState = lineEndState;
        for (int li = startLineIndex + 1; li < lines.Length; li++)
        {
            var lexed = LexJavaScriptLine(lines[li], lexState);
            lexState = lexed.EndState;
            var nextSan = lexed.SanitizedLine;
            for (int c = 0; c < nextSan.Length; c++)
            {
                var ch = nextSan[c];
                if (char.IsWhiteSpace(ch))
                    continue;
                if (ch == '{')
                {
                    openBraceLineIndex = li;
                    openBraceColumn = c;
                    return true;
                }
                return false;
            }
        }

        return false;
    }

    private static List<JavaScriptClassScanTarget> GetJavaScriptTypeScriptExistingClassScanTargets(string lang, string[] lines, List<SymbolRecord> symbols)
    {
        return symbols
            .Where(s => s.Kind is "class" or "interface" && s.BodyStartLine != null && s.BodyEndLine != null)
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .Select(s => CreateJavaScriptClassScanTarget(
                lines,
                lang,
                s.StartLine - 1,
                FindJavaScriptTypeScriptSymbolStartColumn(lines[s.StartLine - 1], s.Signature),
                s.BodyStartLine,
                s.BodyEndLine,
                s.Kind,
                s.Name))
            .ToList();
    }

    private static List<JavaScriptClassScanTarget> CollectJavaScriptTypeScriptSyntheticClassScanTargets(long fileId, string lang, string[] lines, List<SymbolRecord> symbols, JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var targets = new List<JavaScriptClassScanTarget>();
        var lexState = new JavaScriptLexState();
        for (int i = 0; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var lineOffset = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (lineOffset >= 0 && lineOffset < sanitizedLine.Length)
            {
                TryAddJavaScriptTypeScriptSyntheticClassTarget(fileId, lang, lines, symbols, targets, i, lineOffset, sanitizedLine, privateScopeColumns);
                lineOffset = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, lineOffset + 1);
            }
        }

        return targets
            .OrderBy(t => t.StartIndex)
            .ThenByDescending(t => t.ScanEndExclusive)
            .ToList();
    }

    private static bool IsInsideJavaScriptTypeScriptPrivateScope(Stack<JavaScriptScopeKind> scopeStack)
    {
        return scopeStack.Any(scopeKind => scopeKind is JavaScriptScopeKind.Function or JavaScriptScopeKind.StaticBlock);
    }

    private static JavaScriptScopePrivacyFlags GetJavaScriptTypeScriptPrivacyFlags(Stack<JavaScriptScopeKind> scopeStack, bool arrowExpressionActive)
    {
        var flags = JavaScriptScopePrivacyFlags.None;
        if (arrowExpressionActive || IsInsideJavaScriptTypeScriptPrivateScope(scopeStack))
            flags |= JavaScriptScopePrivacyFlags.FunctionLike;
        if (scopeStack.Any(scopeKind => scopeKind == JavaScriptScopeKind.Block))
            flags |= JavaScriptScopePrivacyFlags.Block;
        if (scopeStack.Any(scopeKind => scopeKind == JavaScriptScopeKind.Namespace))
            flags |= JavaScriptScopePrivacyFlags.Namespace;

        return flags;
    }

    private static bool IsInsideJavaScriptTypeScriptMethodContainer(Stack<JavaScriptScopeKind> scopeStack)
    {
        return scopeStack.Count > 0 && scopeStack.Peek() is JavaScriptScopeKind.Class or JavaScriptScopeKind.Object;
    }

    private static void BeginJavaScriptTypeScriptFunctionHeader(ref JavaScriptTypeScriptFunctionHeaderState state)
    {
        state = new JavaScriptTypeScriptFunctionHeaderState
        {
            Active = true,
        };
    }

    private static JavaScriptTypeScriptFunctionHeaderConsumeResult ConsumeJavaScriptTypeScriptFunctionHeaderChar(
        ref JavaScriptTypeScriptFunctionHeaderState state,
        string sanitizedLine,
        int column,
        string lang,
        out int advanceColumns)
    {
        advanceColumns = 0;
        if (!state.Active)
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.NotActive;

        var ch = sanitizedLine[column];
        if (char.IsWhiteSpace(ch))
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;

        if (state.InReturnType)
        {
            if (ch == ';'
                && state.ReturnParenDepth == 0
                && state.ReturnBracketDepth == 0
                && state.ReturnAngleDepth == 0
                && state.ReturnBraceDepth == 0)
            {
                state = default;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '(')
            {
                state.ReturnParenDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "(";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == ')' && state.ReturnParenDepth > 0)
            {
                state.ReturnParenDepth--;
                state.PreviousReturnToken = ")";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '[')
            {
                state.ReturnBracketDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "[";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == ']' && state.ReturnBracketDepth > 0)
            {
                state.ReturnBracketDepth--;
                state.PreviousReturnToken = "]";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '<')
            {
                state.ReturnAngleDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "<";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '>' && state.ReturnAngleDepth > 0)
            {
                state.ReturnAngleDepth--;
                state.PreviousReturnToken = ">";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '{')
            {
                if (state.ReturnParenDepth == 0
                    && state.ReturnBracketDepth == 0
                    && state.ReturnAngleDepth == 0
                    && state.ReturnBraceDepth == 0)
                {
                    if (CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(state.PreviousReturnToken))
                    {
                        state.ReturnBraceDepth++;
                        state.ReturnSawToken = true;
                        state.PreviousReturnToken = "{";
                        return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
                    }

                    if (state.ReturnSawToken)
                    {
                        state = default;
                        return JavaScriptTypeScriptFunctionHeaderConsumeResult.BodyStart;
                    }
                }

                state.ReturnBraceDepth++;
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "{";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '}' && state.ReturnBraceDepth > 0)
            {
                state.ReturnBraceDepth--;
                state.PreviousReturnToken = "}";
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch is '?' or ':' or '|' or '&' or ',')
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = ch.ToString();
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = "=>";
                advanceColumns = 1;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            var returnTypeIndex = column;
            if (TrySkipTypeScriptTypeToken(sanitizedLine, ref returnTypeIndex, out var returnTypeToken))
            {
                state.ReturnSawToken = true;
                state.PreviousReturnToken = returnTypeToken;
                advanceColumns = returnTypeIndex - column - 1;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
            }

            state.ReturnSawToken = true;
            state.PreviousReturnToken = ch.ToString();
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (lang == "typescript" && state.SawParameterList && ch == ':')
        {
            state.InReturnType = true;
            state.ReturnParenDepth = 0;
            state.ReturnBracketDepth = 0;
            state.ReturnAngleDepth = 0;
            state.ReturnBraceDepth = 0;
            state.ReturnSawToken = false;
            state.PreviousReturnToken = ":";
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '(')
        {
            state.ParenDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ')' && state.ParenDepth > 0)
        {
            state.ParenDepth--;
            if (state.ParenDepth == 0 && state.BracketDepth == 0 && state.BraceDepth == 0)
                state.SawParameterList = true;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '[' && (state.ParenDepth > 0 || state.BracketDepth > 0 || state.BraceDepth > 0 || !state.SawParameterList))
        {
            state.BracketDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ']' && state.BracketDepth > 0)
        {
            state.BracketDepth--;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '{')
        {
            if (state.SawParameterList && state.ParenDepth == 0 && state.BracketDepth == 0 && state.BraceDepth == 0)
            {
                state = default;
                return JavaScriptTypeScriptFunctionHeaderConsumeResult.BodyStart;
            }

            state.BraceDepth++;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == '}' && state.BraceDepth > 0)
        {
            state.BraceDepth--;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        if (ch == ';')
        {
            state = default;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        var tokenIndex = column;
        if (TrySkipTypeScriptTypeToken(sanitizedLine, ref tokenIndex, out _))
        {
            advanceColumns = tokenIndex - column - 1;
            return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
        }

        return JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed;
    }

    private static JavaScriptScopePrivacyFlags[][] BuildJavaScriptTypeScriptPrivateScopeColumns(string[] lines, string lang)
    {
        var privateColumns = new JavaScriptScopePrivacyFlags[lines.Length][];
        var lexState = new JavaScriptLexState();
        var scopeStack = new Stack<JavaScriptScopeKind>();
        var pendingFunctionScope = false;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var pendingStaticBlockScope = false;
        var pendingClassScope = false;
        var pendingNamespaceScope = false;
        var pendingConciseMethodScope = false;
        var pendingConciseMethodReturnType = false;
        var conciseMethodReturnParenDepth = 0;
        var conciseMethodReturnBracketDepth = 0;
        var conciseMethodReturnAngleDepth = 0;
        var conciseMethodReturnBraceDepth = 0;
        var conciseMethodReturnSawToken = false;
        string? previousConciseMethodReturnToken = null;
        var pendingArrowBody = false;
        var arrowExpressionActive = false;
        var arrowExpressionParenDepth = 0;
        var arrowExpressionBracketDepth = 0;
        var arrowExpressionBraceDepth = 0;
        var previousTokenKind = JavaScriptPrevTokenKind.None;
        string? previousIdentifier = null;
        char previousSignificantChar = '\0';

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var linePrivateColumns = new JavaScriptScopePrivacyFlags[sanitizedLine.Length];

            if (arrowExpressionActive
                && arrowExpressionBraceDepth == 0
                && arrowExpressionParenDepth == 0
                && arrowExpressionBracketDepth == 0
                && !StartsJavaScriptTypeScriptExpressionContinuation(sanitizedLine))
            {
                arrowExpressionActive = false;
            }

            for (int column = 0; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);

                if (char.IsWhiteSpace(ch))
                    continue;

                if (pendingArrowBody)
                {
                    if (ch == '{')
                    {
                        scopeStack.Push(JavaScriptScopeKind.Function);
                        linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                        pendingArrowBody = false;
                        continue;
                    }

                    arrowExpressionActive = true;
                    linePrivateColumns[column] = JavaScriptScopePrivacyFlags.FunctionLike;
                    pendingArrowBody = false;
                }

                if (pendingConciseMethodReturnType)
                {
                    if (ch == '(')
                    {
                        conciseMethodReturnParenDepth++;
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "(";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == ')' && conciseMethodReturnParenDepth > 0)
                    {
                        conciseMethodReturnParenDepth--;
                        previousConciseMethodReturnToken = ")";
                        previousTokenKind = JavaScriptPrevTokenKind.CloseParen;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '[')
                    {
                        conciseMethodReturnBracketDepth++;
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "[";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == ']' && conciseMethodReturnBracketDepth > 0)
                    {
                        conciseMethodReturnBracketDepth--;
                        previousConciseMethodReturnToken = "]";
                        previousTokenKind = JavaScriptPrevTokenKind.CloseBracket;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '<')
                    {
                        conciseMethodReturnAngleDepth++;
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "<";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '>' && conciseMethodReturnAngleDepth > 0)
                    {
                        conciseMethodReturnAngleDepth--;
                        previousConciseMethodReturnToken = ">";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '{')
                    {
                        if (conciseMethodReturnParenDepth == 0
                            && conciseMethodReturnBracketDepth == 0
                            && conciseMethodReturnAngleDepth == 0
                            && conciseMethodReturnBraceDepth == 0)
                        {
                            if (CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(previousConciseMethodReturnToken))
                            {
                                conciseMethodReturnBraceDepth++;
                                conciseMethodReturnSawToken = true;
                                previousConciseMethodReturnToken = "{";
                                previousTokenKind = JavaScriptPrevTokenKind.Other;
                                previousIdentifier = null;
                                previousSignificantChar = ch;
                                continue;
                            }

                            if (conciseMethodReturnSawToken)
                            {
                                linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                                scopeStack.Push(JavaScriptScopeKind.Function);
                                pendingConciseMethodScope = false;
                                pendingConciseMethodReturnType = false;
                                conciseMethodReturnParenDepth = 0;
                                conciseMethodReturnBracketDepth = 0;
                                conciseMethodReturnAngleDepth = 0;
                                conciseMethodReturnBraceDepth = 0;
                                conciseMethodReturnSawToken = false;
                                previousConciseMethodReturnToken = null;
                                previousTokenKind = JavaScriptPrevTokenKind.CloseBrace;
                                previousIdentifier = null;
                                previousSignificantChar = ch;
                                continue;
                            }
                        }

                        conciseMethodReturnBraceDepth++;
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "{";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '}' && conciseMethodReturnBraceDepth > 0)
                    {
                        conciseMethodReturnBraceDepth--;
                        previousConciseMethodReturnToken = "}";
                        previousTokenKind = JavaScriptPrevTokenKind.CloseBrace;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '?' || ch == ':' || ch == '|' || ch == '&' || ch == ',')
                    {
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = ch.ToString();
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = ch;
                        continue;
                    }

                    if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
                    {
                        linePrivateColumns[column + 1] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = "=>";
                        previousTokenKind = JavaScriptPrevTokenKind.Other;
                        previousIdentifier = null;
                        previousSignificantChar = '>';
                        column++;
                        continue;
                    }

                    if (IsJavaScriptTypeScriptIdentifierStart(ch))
                    {
                        var returnTokenStart = column;
                        column++;
                        while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                        {
                            linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                            column++;
                        }

                        conciseMethodReturnSawToken = true;
                        previousConciseMethodReturnToken = sanitizedLine[returnTokenStart..column];
                        previousTokenKind = JavaScriptPrevTokenKind.Identifier;
                        previousIdentifier = previousConciseMethodReturnToken;
                        previousSignificantChar = sanitizedLine[column - 1];
                        column--;
                        continue;
                    }

                    conciseMethodReturnSawToken = true;
                    previousConciseMethodReturnToken = ch.ToString();
                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (pendingFunctionScope)
                {
                    var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                        ref functionHeaderState,
                        sanitizedLine,
                        column,
                        lang,
                        out var functionHeaderAdvanceColumns);
                    if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                    {
                        previousTokenKind = ch switch
                        {
                            ')' => JavaScriptPrevTokenKind.CloseParen,
                            ']' => JavaScriptPrevTokenKind.CloseBracket,
                            _ => JavaScriptPrevTokenKind.Other,
                        };
                        previousIdentifier = null;
                        previousSignificantChar = sanitizedLine[Math.Min(column + functionHeaderAdvanceColumns, sanitizedLine.Length - 1)];
                        column += functionHeaderAdvanceColumns;
                        continue;
                    }
                }

                if (IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    column++;
                    while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                    {
                        linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);

                        column++;
                    }

                    var token = sanitizedLine[tokenStart..column];
                    if (token == "function")
                    {
                        pendingFunctionScope = true;
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        pendingStaticBlockScope = false;
                        pendingConciseMethodScope = false;
                        pendingConciseMethodReturnType = false;
                    }
                    else if (token == "static")
                    {
                        pendingStaticBlockScope = IsInsideJavaScriptTypeScriptMethodContainer(scopeStack);
                    }
                    else if (token == "class")
                    {
                        pendingClassScope = true;
                        pendingStaticBlockScope = false;
                        pendingConciseMethodScope = false;
                        pendingConciseMethodReturnType = false;
                    }
                    else if (lang == "typescript" && token is "namespace" or "module")
                    {
                        pendingNamespaceScope = true;
                        pendingStaticBlockScope = false;
                    }
                    else
                    {
                        pendingStaticBlockScope = false;
                    }

                    previousTokenKind = JavaScriptPrevTokenKind.Identifier;
                    previousIdentifier = token;
                    previousSignificantChar = sanitizedLine[column - 1];
                    column--;
                    continue;
                }

                if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
                {
                    linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                    linePrivateColumns[column + 1] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                    pendingArrowBody = true;
                    pendingFunctionScope = false;
                    functionHeaderState = default;
                    pendingStaticBlockScope = false;
                    pendingClassScope = false;
                    pendingNamespaceScope = false;
                    pendingConciseMethodScope = false;
                    pendingConciseMethodReturnType = false;
                    conciseMethodReturnParenDepth = 0;
                    conciseMethodReturnBracketDepth = 0;
                    conciseMethodReturnAngleDepth = 0;
                    conciseMethodReturnBraceDepth = 0;
                    conciseMethodReturnSawToken = false;
                    previousConciseMethodReturnToken = null;
                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = '>';
                    column++;
                    continue;
                }

                if (ch == '(')
                {
                    if (arrowExpressionActive)
                        arrowExpressionParenDepth++;

                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == ')')
                {
                    if (arrowExpressionActive && arrowExpressionParenDepth > 0)
                        arrowExpressionParenDepth--;

                    if (IsInsideJavaScriptTypeScriptMethodContainer(scopeStack))
                    {
                        pendingConciseMethodScope = true;
                        pendingConciseMethodReturnType = false;
                        conciseMethodReturnParenDepth = 0;
                        conciseMethodReturnBracketDepth = 0;
                        conciseMethodReturnAngleDepth = 0;
                        conciseMethodReturnBraceDepth = 0;
                        conciseMethodReturnSawToken = false;
                        previousConciseMethodReturnToken = null;
                    }

                    previousTokenKind = JavaScriptPrevTokenKind.CloseParen;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == '[')
                {
                    if (arrowExpressionActive)
                        arrowExpressionBracketDepth++;

                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == ']')
                {
                    if (arrowExpressionActive && arrowExpressionBracketDepth > 0)
                        arrowExpressionBracketDepth--;

                    previousTokenKind = JavaScriptPrevTokenKind.CloseBracket;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == '{')
                {
                    var scopeKind = JavaScriptScopeKind.Other;
                    if (pendingFunctionScope)
                        scopeKind = JavaScriptScopeKind.Function;
                    else if (pendingConciseMethodScope)
                        scopeKind = JavaScriptScopeKind.Function;
                    else if (pendingStaticBlockScope)
                        scopeKind = JavaScriptScopeKind.StaticBlock;
                    else if (pendingClassScope)
                        scopeKind = JavaScriptScopeKind.Class;
                    else if (pendingNamespaceScope)
                        scopeKind = JavaScriptScopeKind.Namespace;
                    else if (CanStartJavaScriptTypeScriptObjectLiteral(previousTokenKind, previousIdentifier, previousSignificantChar))
                        scopeKind = JavaScriptScopeKind.Object;
                    else
                        scopeKind = JavaScriptScopeKind.Block;

                    linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);

                    scopeStack.Push(scopeKind);
                    if (arrowExpressionActive)
                        arrowExpressionBraceDepth++;

                    pendingFunctionScope = false;
                    functionHeaderState = default;
                    pendingStaticBlockScope = false;
                    pendingClassScope = false;
                    pendingNamespaceScope = false;
                    pendingConciseMethodScope = false;
                    pendingConciseMethodReturnType = false;
                    conciseMethodReturnParenDepth = 0;
                    conciseMethodReturnBracketDepth = 0;
                    conciseMethodReturnAngleDepth = 0;
                    conciseMethodReturnBraceDepth = 0;
                    conciseMethodReturnSawToken = false;
                    previousConciseMethodReturnToken = null;
                    previousTokenKind = JavaScriptPrevTokenKind.CloseBrace;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == '}')
                {
                    if (arrowExpressionActive)
                    {
                        linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                        if (arrowExpressionBraceDepth > 0)
                            arrowExpressionBraceDepth--;
                    }

                    if (scopeStack.Count > 0)
                        scopeStack.Pop();

                    pendingFunctionScope = false;
                    functionHeaderState = default;
                    pendingStaticBlockScope = false;
                    pendingClassScope = false;
                    pendingNamespaceScope = false;
                    pendingConciseMethodScope = false;
                    pendingConciseMethodReturnType = false;
                    conciseMethodReturnParenDepth = 0;
                    conciseMethodReturnBracketDepth = 0;
                    conciseMethodReturnAngleDepth = 0;
                    conciseMethodReturnBraceDepth = 0;
                    conciseMethodReturnSawToken = false;
                    previousConciseMethodReturnToken = null;
                    previousTokenKind = JavaScriptPrevTokenKind.CloseBrace;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch is ';' or ',')
                {
                    if (arrowExpressionActive
                        && arrowExpressionBraceDepth == 0
                        && arrowExpressionParenDepth == 0
                        && arrowExpressionBracketDepth == 0)
                    {
                        linePrivateColumns[column] = GetJavaScriptTypeScriptPrivacyFlags(scopeStack, arrowExpressionActive);
                        arrowExpressionActive = false;
                    }

                    pendingFunctionScope = false;
                    functionHeaderState = default;
                    pendingStaticBlockScope = false;
                    pendingClassScope = false;
                    pendingNamespaceScope = false;
                    pendingConciseMethodScope = false;
                    pendingConciseMethodReturnType = false;
                    conciseMethodReturnParenDepth = 0;
                    conciseMethodReturnBracketDepth = 0;
                    conciseMethodReturnAngleDepth = 0;
                    conciseMethodReturnBraceDepth = 0;
                    conciseMethodReturnSawToken = false;
                    previousConciseMethodReturnToken = null;
                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (ch == ':')
                {
                    if (pendingConciseMethodScope && lang == "typescript")
                    {
                        pendingConciseMethodReturnType = true;
                        conciseMethodReturnParenDepth = 0;
                        conciseMethodReturnBracketDepth = 0;
                        conciseMethodReturnAngleDepth = 0;
                        conciseMethodReturnBraceDepth = 0;
                        conciseMethodReturnSawToken = false;
                        previousConciseMethodReturnToken = ":";
                    }

                    previousTokenKind = JavaScriptPrevTokenKind.Other;
                    previousIdentifier = null;
                    previousSignificantChar = ch;
                    continue;
                }

                if (pendingStaticBlockScope && ch != '{')
                    pendingStaticBlockScope = false;
                if (pendingNamespaceScope && ch is not '{' and not '.')
                    pendingNamespaceScope = false;

                previousTokenKind = JavaScriptPrevTokenKind.Other;
                previousIdentifier = null;
                previousSignificantChar = ch;
            }

            privateColumns[lineIndex] = linePrivateColumns;
        }

        return privateColumns;
    }

    private static bool CanStartJavaScriptTypeScriptObjectLiteral(
        JavaScriptPrevTokenKind previousTokenKind,
        string? previousIdentifier,
        char previousSignificantChar)
    {
        if (previousSignificantChar is '=' or '(' or '[' or ',' or ':' or '?' or '!')
            return true;

        if (previousIdentifier is "return" or "throw" or "case" or "else")
            return true;

        return previousTokenKind == JavaScriptPrevTokenKind.None;
    }

    private static bool StartsJavaScriptTypeScriptExpressionContinuation(string sanitizedLine)
    {
        var index = 0;
        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        if (index >= sanitizedLine.Length)
            return false;

        var remaining = sanitizedLine[index..];
        if (remaining.StartsWith(".", StringComparison.Ordinal)
            || remaining.StartsWith("?.", StringComparison.Ordinal)
            || remaining.StartsWith("[", StringComparison.Ordinal)
            || remaining.StartsWith("(", StringComparison.Ordinal)
            || remaining.StartsWith("`", StringComparison.Ordinal)
            || remaining.StartsWith("?.[", StringComparison.Ordinal))
            return true;

        if (Regex.IsMatch(remaining, @"^(?:\+\+|--|\+|-|\*|/|%|\*\*|&&|\|\||\?\?|\?|:|==|===|!=|!==|<=|>=|<|>|=>|&|\||\^|<<|>>|>>>|,)\b?"))
            return true;

        if (Regex.IsMatch(remaining, @"^(?:instanceof|in|as|satisfies)\b"))
            return true;

        return false;
    }

    private static bool IsAnonymousJavaScriptTypeScriptDefaultClassDeclaration(string[] lines, int startIndex, int startColumn)
    {
        if (!TryGetAnonymousJavaScriptTypeScriptDefaultClassToken(lines, startIndex, startColumn, "javascript", out var tokenLineIndex, out var tokenStartColumn))
            return false;

        startIndex = tokenLineIndex;
        startColumn = tokenStartColumn + "class".Length;
        if (!TryAdvanceJavaScriptTypeScriptClassHeaderContinuation(lines, startIndex, startColumn, "javascript", out startIndex, out startColumn))
            return false;

        var lexState = new JavaScriptLexState();
        for (int lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == startIndex ? startColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    column++;
                    while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                        column++;

                    var nextToken = sanitizedLine[tokenStart..column];
                    return nextToken is "extends" or "implements";
                }

                return ch == '{';
            }
        }

        return false;
    }

    private static bool TryGetAnonymousJavaScriptTypeScriptDefaultClassToken(
        string[] lines,
        int startIndex,
        int startColumn,
        string lang,
        out int classLineIndex,
        out int classStartColumn)
    {
        classLineIndex = -1;
        classStartColumn = -1;

        if (!TryGetJavaScriptTypeScriptNextToken(lines, startIndex, startColumn, skipWrappingParens: true, out classLineIndex, out classStartColumn, out var token))
            return false;

        if (lang == "typescript" && token == "abstract")
        {
            if (!TryGetJavaScriptTypeScriptNextToken(lines, classLineIndex, classStartColumn + token.Length, skipWrappingParens: false, out classLineIndex, out classStartColumn, out token))
                return false;
        }

        if (token != "class")
            return false;

        var inspectLineIndex = classLineIndex;
        var inspectStartColumn = classStartColumn + token.Length;
        if (!TryAdvanceJavaScriptTypeScriptClassHeaderContinuation(lines, inspectLineIndex, inspectStartColumn, lang, out inspectLineIndex, out inspectStartColumn))
            return false;

        var lexState = new JavaScriptLexState();
        for (int lineIndex = inspectLineIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == inspectLineIndex ? inspectStartColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    column++;
                    while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                        column++;

                    var nextToken = sanitizedLine[tokenStart..column];
                    return nextToken is "extends" or "implements";
                }

                return ch == '{';
            }
        }

        return false;
    }

    private static bool TryAdvanceJavaScriptTypeScriptClassHeaderContinuation(
        string[] lines,
        int startIndex,
        int startColumn,
        string lang,
        out int nextLineIndex,
        out int nextColumn)
    {
        nextLineIndex = startIndex;
        nextColumn = startColumn;
        if (lang != "typescript")
            return true;

        var lexState = new JavaScriptLexState();
        var sawTypeParameterList = false;
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == startIndex ? startColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (!sawTypeParameterList)
                {
                    if (ch != '<')
                    {
                        nextLineIndex = lineIndex;
                        nextColumn = column;
                        return true;
                    }

                    sawTypeParameterList = true;
                    angleDepth = 1;
                    column++;
                    continue;
                }

                if (ch == '(')
                {
                    parenDepth++;
                    column++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    column++;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    column++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    column++;
                    continue;
                }

                if (ch == '{')
                {
                    braceDepth++;
                    column++;
                    continue;
                }

                if (ch == '}' && braceDepth > 0)
                {
                    braceDepth--;
                    column++;
                    continue;
                }

                if (ch == '<')
                {
                    angleDepth++;
                    column++;
                    continue;
                }

                if (ch == '=' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '>')
                {
                    column += 2;
                    continue;
                }

                if (ch == '>' && angleDepth > 0 && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                {
                    angleDepth--;
                    column++;
                    if (angleDepth == 0)
                    {
                        while (column < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[column]))
                            column++;

                        nextLineIndex = lineIndex;
                        nextColumn = column;
                        return true;
                    }

                    continue;
                }

                column++;
            }
        }

        return !sawTypeParameterList;
    }

    private static bool IsJavaScriptTypeScriptMatchInPrivateScope(
        JavaScriptScopePrivacyFlags[][] privateScopeColumns,
        int lineIndex,
        int startColumn,
        string matchLine,
        bool includeBlockScope)
    {
        if (lineIndex < 0 || lineIndex >= privateScopeColumns.Length)
            return false;

        var linePrivateColumns = privateScopeColumns[lineIndex];
        if (linePrivateColumns.Length == 0)
            return false;

        var column = Math.Max(0, startColumn);
        while (column < matchLine.Length && char.IsWhiteSpace(matchLine[column]))
            column++;

        if (column >= linePrivateColumns.Length)
            return false;

        var flags = linePrivateColumns[column];
        if ((flags & JavaScriptScopePrivacyFlags.FunctionLike) != 0)
            return true;

        return includeBlockScope && (flags & JavaScriptScopePrivacyFlags.Block) != 0;
    }

    private static bool IsJavaScriptTypeScriptMatchInNamespaceScope(
        JavaScriptScopePrivacyFlags[][] privateScopeColumns,
        int lineIndex,
        int startColumn,
        string matchLine)
    {
        if (lineIndex < 0 || lineIndex >= privateScopeColumns.Length)
            return false;

        var linePrivateColumns = privateScopeColumns[lineIndex];
        if (linePrivateColumns.Length == 0)
            return false;

        var column = Math.Max(0, startColumn);
        while (column < matchLine.Length && char.IsWhiteSpace(matchLine[column]))
            column++;

        if (column >= linePrivateColumns.Length)
            return false;

        return (linePrivateColumns[column] & JavaScriptScopePrivacyFlags.Namespace) != 0;
    }

    private static bool IsJavaScriptTypeScriptControlFlowHeader(string sanitizedLine, int startColumn)
    {
        var index = Math.Max(0, startColumn);
        if (index >= sanitizedLine.Length || !IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
            return false;

        var tokenStart = index;
        index++;
        while (index < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index]))
            index++;

        var token = sanitizedLine[tokenStart..index];
        if (!JavaScriptTypeScriptControlFlowHeaderKeywords.Contains(token))
            return false;

        if (index >= sanitizedLine.Length || !char.IsWhiteSpace(sanitizedLine[index]))
            return false;

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        return index < sanitizedLine.Length && sanitizedLine[index] == '(';
    }

    private static void ExtractJavaScriptTypeScriptBareMethodsInTargets(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> classScanTargets)
    {
        foreach (var classScanTarget in classScanTargets)
            ExtractJavaScriptTypeScriptBareMethodsInClass(fileId, lang, lines, symbols, classScanTarget);
    }

    private static void TryAddJavaScriptTypeScriptSyntheticClassTarget(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> targets,
        int startIndex,
        int startColumn,
        string sanitizedLine,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        var lineRemainder = sanitizedLine[startColumn..];
        var anonymousDefaultMatch = JavaScriptTypeScriptAnonymousDefaultExportRegex.Match(lineRemainder);
        if (anonymousDefaultMatch.Success)
        {
            if (!TryGetAnonymousJavaScriptTypeScriptDefaultClassToken(
                lines,
                startIndex,
                startColumn + anonymousDefaultMatch.Index + anonymousDefaultMatch.Length,
                lang,
                out var classTokenLineIndex,
                out var classTokenStartColumn))
                return;

            if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, startIndex, startColumn + anonymousDefaultMatch.Index, sanitizedLine, includeBlockScope: false))
                return;

            if (TryGetGroup(anonymousDefaultMatch, "visibility") != "export"
                && IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, startIndex, startColumn + anonymousDefaultMatch.Index, sanitizedLine))
            {
                return;
            }

        AddJavaScriptTypeScriptSyntheticClassTarget(
            fileId,
            lang,
            lines,
            symbols,
            targets,
            startIndex,
            startColumn + anonymousDefaultMatch.Index,
            classTokenLineIndex,
            classTokenStartColumn,
            containerName: "default",
            visibility: TryGetGroup(anonymousDefaultMatch, "visibility"));
            return;
        }

        if (lang == "typescript")
        {
            var exportEqualsMatch = TypeScriptExportEqualsRegex.Match(lineRemainder);
            if (exportEqualsMatch.Success)
            {
                if (!IsJavaScriptTypeScriptClassExpressionDeclaration(lines, startIndex, startColumn + exportEqualsMatch.Index + exportEqualsMatch.Length))
                    return;

                if (!TryGetJavaScriptTypeScriptNextToken(
                    lines,
                    startIndex,
                    startColumn + exportEqualsMatch.Index + exportEqualsMatch.Length,
                    skipWrappingParens: true,
                    out var exportEqualsClassTokenLineIndex,
                    out var exportEqualsClassTokenStartColumn,
                    out _))
                {
                    return;
                }

                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, startIndex, startColumn + exportEqualsMatch.Index, sanitizedLine, includeBlockScope: false))
                    return;

                if (IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, startIndex, startColumn + exportEqualsMatch.Index, sanitizedLine))
                    return;

                AddJavaScriptTypeScriptSyntheticClassTarget(
                    fileId,
                    lang,
                    lines,
                    symbols,
                    targets,
                    startIndex,
                    startColumn + exportEqualsMatch.Index,
                    exportEqualsClassTokenLineIndex,
                    exportEqualsClassTokenStartColumn,
                    containerName: "default",
                    visibility: "export");
                return;
            }
        }

        var classExpressionBindingMatch = JavaScriptTypeScriptClassExpressionBindingRegex.Match(lineRemainder);
        if (!classExpressionBindingMatch.Success)
            return;

        if (!IsJavaScriptTypeScriptClassExpressionDeclaration(lines, startIndex, startColumn + classExpressionBindingMatch.Index + classExpressionBindingMatch.Length))
            return;

        if (!TryGetJavaScriptTypeScriptNextToken(
            lines,
            startIndex,
            startColumn + classExpressionBindingMatch.Index + classExpressionBindingMatch.Length,
            skipWrappingParens: true,
            out var classExpressionTokenLineIndex,
            out var classExpressionTokenStartColumn,
            out _))
        {
            return;
        }

        var includeBlockScope = classExpressionBindingMatch.Groups["bindingKind"].Success
            && classExpressionBindingMatch.Groups["bindingKind"].Value is "const" or "let";
        if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, startIndex, startColumn + classExpressionBindingMatch.Index, sanitizedLine, includeBlockScope))
            return;

        if (TryGetGroup(classExpressionBindingMatch, "visibility") != "export"
            && IsJavaScriptTypeScriptMatchInNamespaceScope(privateScopeColumns, startIndex, startColumn + classExpressionBindingMatch.Index, sanitizedLine))
        {
            return;
        }

        var containerName = TryGetGroup(classExpressionBindingMatch, "alias")
            ?? TryGetGroup(classExpressionBindingMatch, "exportsAlias")
            ?? TryGetGroup(classExpressionBindingMatch, "moduleExportsAlias")
            ?? (classExpressionBindingMatch.Groups["moduleExports"].Success ? "default" : null)
            ?? "class";
        AddJavaScriptTypeScriptSyntheticClassTarget(
            fileId,
            lang,
            lines,
            symbols,
            targets,
            startIndex,
            startColumn + classExpressionBindingMatch.Index,
            classExpressionTokenLineIndex,
            classExpressionTokenStartColumn,
            containerName,
            TryGetGroup(classExpressionBindingMatch, "visibility"));
    }

    private static bool IsJavaScriptTypeScriptClassExpressionDeclaration(string[] lines, int startIndex, int startColumn)
    {
        return TryGetJavaScriptTypeScriptNextToken(lines, startIndex, startColumn, skipWrappingParens: true, out _, out _, out var token)
            && token == "class";
    }

    private static void AddJavaScriptTypeScriptSyntheticClassTarget(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> targets,
        int declarationStartIndex,
        int declarationStartColumn,
        int classTokenLineIndex,
        int classTokenStartColumn,
        string containerName,
        string? visibility)
    {
        var (endLine, bodyStartLine, bodyEndLine) = ResolveRange(lines, classTokenLineIndex, BodyStyle.Brace, lang, classTokenStartColumn);
        if (bodyStartLine == null || bodyEndLine == null)
            return;

        var existingClass = symbols.FirstOrDefault(s => s.Kind == "class" && s.Line == declarationStartIndex + 1 && s.Name == containerName);
        if (existingClass == null)
        {
            symbols.Add(new SymbolRecord
            {
                FileId = fileId,
                Kind = "class",
                Name = containerName,
                Line = declarationStartIndex + 1,
                StartLine = declarationStartIndex + 1,
                EndLine = Math.Max(declarationStartIndex + 1, endLine),
                BodyStartLine = bodyStartLine,
                BodyEndLine = bodyEndLine,
                Signature = BuildJavaScriptTypeScriptSyntheticClassSignature(lines, declarationStartIndex, declarationStartColumn, classTokenLineIndex, classTokenStartColumn, bodyStartLine, bodyEndLine, lang),
                Visibility = visibility,
            });
        }

        var candidate = CreateJavaScriptClassScanTarget(lines, lang, classTokenLineIndex, classTokenStartColumn, bodyStartLine, bodyEndLine, "class", containerName);
        if (!targets.Any(t => t.StartIndex == candidate.StartIndex
            && t.StartColumn == candidate.StartColumn
            && t.ScanStartIndex == candidate.ScanStartIndex
            && t.ScanEndExclusive == candidate.ScanEndExclusive
            && t.FirstLineScanOffset == candidate.FirstLineScanOffset
            && t.ContainerKind == candidate.ContainerKind
            && t.ContainerName == candidate.ContainerName))
        {
            targets.Add(candidate);
        }
    }

    private static string BuildJavaScriptTypeScriptSyntheticClassSignature(
        string[] lines,
        int declarationStartIndex,
        int declarationStartColumn,
        int classTokenLineIndex,
        int classTokenStartColumn,
        int? bodyStartLine,
        int? bodyEndLine,
        string lang)
    {
        var line = lines[declarationStartIndex];
        if (declarationStartColumn >= line.Length)
            return string.Empty;

        if (bodyEndLine == declarationStartIndex + 1)
        {
            var sameLineEndColumn = FindJavaScriptTypeScriptSameLineBraceEndColumn(line, classTokenStartColumn, lang);
            if (sameLineEndColumn >= declarationStartColumn)
                return line[declarationStartColumn..(sameLineEndColumn + 1)].Trim();
        }

        if (bodyStartLine == null)
            return line[declarationStartColumn..].Trim();

        var bodyStartIndex = bodyStartLine.Value - 1;
        var bodyOpenBraceColumn = FindJavaScriptBodyOpenBraceIndex(lines, classTokenLineIndex, bodyStartIndex, lang, classTokenStartColumn);
        if (bodyOpenBraceColumn < 0)
            return line[declarationStartColumn..].Trim();

        var signatureBuilder = new System.Text.StringBuilder();
        for (int lineIndex = declarationStartIndex; lineIndex <= bodyStartIndex; lineIndex++)
        {
            var sourceLine = lines[lineIndex];
            var startColumn = lineIndex == declarationStartIndex
                ? Math.Min(declarationStartColumn, sourceLine.Length)
                : 0;
            var endExclusive = lineIndex == bodyStartIndex
                ? Math.Min(bodyOpenBraceColumn + 1, sourceLine.Length)
                : sourceLine.Length;
            if (startColumn >= endExclusive)
                continue;

            var segment = sourceLine[startColumn..endExclusive].Trim();
            if (segment.Length == 0)
                continue;

            if (signatureBuilder.Length > 0)
                signatureBuilder.Append(' ');

            signatureBuilder.Append(segment);
        }

        return signatureBuilder.Length > 0
            ? signatureBuilder.ToString()
            : line[declarationStartColumn..].Trim();
    }

    private static JavaScriptClassScanTarget CreateJavaScriptClassScanTarget(string[] lines, string lang, int startIndex, int startColumn, int? bodyStartLine, int? bodyEndLine, string containerKind, string containerName, bool isExported = false)
    {
        var scanStartIndex = bodyStartLine!.Value - 1;
        var scanEndExclusive = bodyEndLine!.Value;
        var firstLineScanOffset = FindJavaScriptBodyOpenBraceIndex(lines, startIndex, scanStartIndex, lang, startColumn);
        if (firstLineScanOffset >= 0)
            firstLineScanOffset++;
        else
            firstLineScanOffset = 0;

        return new JavaScriptClassScanTarget(
            startIndex,
            startColumn,
            scanStartIndex,
            scanEndExclusive,
            firstLineScanOffset,
            containerKind,
            containerName,
            isExported);
    }

    private static void ExtractJavaScriptTypeScriptBareMethodsInClass(
        long fileId,
        string lang,
        string[] lines,
        List<SymbolRecord> symbols,
        JavaScriptClassScanTarget classScanTarget)
    {
        if (classScanTarget.ScanStartIndex >= classScanTarget.ScanEndExclusive)
            return;

        var scanStartIndex = classScanTarget.ScanStartIndex;
        var scanEndExclusive = classScanTarget.ScanEndExclusive;
        var nestedBraceDepth = 0;
        var inFieldInitializer = false;
        var initializerParenDepth = 0;
        var initializerBracketDepth = 0;
        var initializerBraceDepth = 0;
        var lexState = new JavaScriptLexState();
        var seenMethodStarts = new HashSet<(int Line, int Column)>();
        var pendingHeaderEndLineIndex = -1;
        var pendingHeaderEndColumn = -1;
        var pendingBodyStartLineIndex = -1;
        var pendingBodyStartColumn = -1;

        for (int i = scanStartIndex; i < scanEndExclusive; i++)
        {
            var line = lines[i];
            var lexedLine = LexJavaScriptLine(line, lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            if (pendingHeaderEndLineIndex >= 0)
            {
                if (i < pendingHeaderEndLineIndex)
                    continue;
            }

            if (pendingBodyStartLineIndex >= 0)
            {
                if (i < pendingBodyStartLineIndex)
                    continue;

                if (i == pendingBodyStartLineIndex)
                {
                    if (pendingBodyStartColumn >= 0 && pendingBodyStartColumn < sanitizedLine.Length)
                    {
                        nestedBraceDepth += CountBraces(sanitizedLine[pendingBodyStartColumn..]);
                        if (nestedBraceDepth < 0)
                            nestedBraceDepth = 0;
                    }

                    pendingBodyStartLineIndex = -1;
                    pendingBodyStartColumn = -1;
                    continue;
                }
            }

            var scanStartColumn = i == scanStartIndex
                ? Math.Min(classScanTarget.FirstLineScanOffset, sanitizedLine.Length)
                : 0;
            if (pendingHeaderEndLineIndex == i)
            {
                scanStartColumn = Math.Max(scanStartColumn, Math.Min(pendingHeaderEndColumn + 1, sanitizedLine.Length));
                pendingHeaderEndLineIndex = -1;
                pendingHeaderEndColumn = -1;
            }

            if (inFieldInitializer
                && initializerParenDepth == 0
                && initializerBracketDepth == 0
                && initializerBraceDepth == 0)
            {
                var continuationInput = scanStartColumn >= sanitizedLine.Length
                    ? string.Empty
                    : sanitizedLine[scanStartColumn..];
                if (continuationInput.Any(ch => !char.IsWhiteSpace(ch))
                    && !StartsJavaScriptTypeScriptFieldInitializerContinuation(continuationInput, lang))
                {
                    inFieldInitializer = false;
                }
            }

            var column = scanStartColumn;
            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (classScanTarget.ContainerKind == "object"
                    && nestedBraceDepth == 0
                    && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var propertyStartColumn = column;
                    var propertyEndColumn = propertyStartColumn + 1;
                    while (propertyEndColumn < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[propertyEndColumn]))
                        propertyEndColumn++;

                    var propertyScanColumn = propertyEndColumn;
                    while (propertyScanColumn < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[propertyScanColumn]))
                        propertyScanColumn++;

                    if (propertyScanColumn < sanitizedLine.Length && sanitizedLine[propertyScanColumn] == ':')
                    {
                        var valueStartColumn = propertyScanColumn + 1;
                        while (valueStartColumn < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[valueStartColumn]))
                            valueStartColumn++;

                        if (valueStartColumn < sanitizedLine.Length
                            && StartsJavaScriptTypeScriptFunctionAssignmentValue(sanitizedLine[valueStartColumn..]))
                        {
                            var propertyName = sanitizedLine[propertyStartColumn..propertyEndColumn];
                            if (seenMethodStarts.Add((i + 1, propertyStartColumn)))
                            {
                                var propertyBodyOpenBraceLineIndex = -1;
                                var propertyBodyOpenBraceColumn = -1;
                                int propertyEndLine;
                                int? propertyBodyStartLine;
                                int? propertyBodyEndLine;
                                int propertySameLineEndColumn;
                                if (TryFindJavaScriptTypeScriptAssignedFunctionBodyOpenBrace(
                                    lines,
                                    i,
                                    valueStartColumn,
                                    lang,
                                    out var foundPropertyBodyOpenBraceLineIndex,
                                    out var foundPropertyBodyOpenBraceColumn))
                                {
                                    propertyBodyOpenBraceLineIndex = foundPropertyBodyOpenBraceLineIndex;
                                    propertyBodyOpenBraceColumn = foundPropertyBodyOpenBraceColumn;
                                    (propertyEndLine, propertyBodyStartLine, propertyBodyEndLine) = ResolveRange(
                                        lines, propertyBodyOpenBraceLineIndex, BodyStyle.Brace, lang, propertyBodyOpenBraceColumn);
                                    propertySameLineEndColumn = propertyBodyEndLine == i + 1
                                        ? FindSameLineBraceEndColumn(line, valueStartColumn, lang, "function")
                                        : -1;
                                }
                                else
                                {
                                    propertyEndLine = i + 1;
                                    propertyBodyStartLine = null;
                                    propertyBodyEndLine = null;
                                    propertySameLineEndColumn = -1;
                                }

                                symbols.Add(new SymbolRecord
                                {
                                    FileId = fileId,
                                    Kind = "function",
                                    Name = propertyName,
                                    Line = i + 1,
                                    StartLine = i + 1,
                                    EndLine = Math.Max(i + 1, propertyEndLine),
                                    BodyStartLine = propertyBodyStartLine,
                                    BodyEndLine = propertyBodyEndLine,
                                    Signature = line.Trim(),
                                    ContainerKind = classScanTarget.ContainerKind,
                                    ContainerName = classScanTarget.ContainerName,
                                    Visibility = classScanTarget.IsExported ? "export" : null,
                                });

                                if (propertySameLineEndColumn >= column)
                                {
                                    column = propertySameLineEndColumn + 1;
                                    continue;
                                }

                                if (propertyBodyStartLine.HasValue
                                    && propertyBodyStartLine.Value - 1 > i)
                                {
                                    pendingBodyStartLineIndex = propertyBodyStartLine.Value - 1;
                                    pendingBodyStartColumn = propertyBodyOpenBraceColumn;
                                    break;
                                }

                                if (propertyBodyStartLine.HasValue
                                    && propertyBodyStartLine.Value - 1 == i
                                    && propertyBodyOpenBraceColumn >= 0
                                    && propertyBodyOpenBraceColumn < sanitizedLine.Length)
                                {
                                    nestedBraceDepth += CountBraces(sanitizedLine[propertyBodyOpenBraceColumn..]);
                                    if (nestedBraceDepth < 0)
                                        nestedBraceDepth = 0;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (inFieldInitializer)
                {
                    AdvanceJavaScriptTypeScriptFieldInitializerState(
                        ref inFieldInitializer,
                        ref initializerParenDepth,
                        ref initializerBracketDepth,
                        ref initializerBraceDepth,
                        ch);
                    column++;
                    continue;
                }

                if (nestedBraceDepth == 0
                    && classScanTarget.ContainerKind is "interface" or "class"
                    && StartsJavaScriptTypeScriptClassMemberAt(sanitizedLine, column))
                {
                    if (lang == "typescript"
                        && classScanTarget.ContainerKind == "class"
                        && TryParseJavaScriptTypeScriptAccessorFieldHeader(
                            sanitizedLine,
                            column,
                            out var accessorName,
                            out var accessorVisibility,
                            out var accessorTypeStartColumn,
                            out var accessorTypeEndColumn,
                            out var accessorHeaderEndColumn,
                            out var accessorHasInitializer))
                    {
                        var accessorStartLine = i + 1;
                        if (seenMethodStarts.Add((accessorStartLine, column)))
                        {
                            var accessorSignatureEnd = accessorHeaderEndColumn < line.Length
                                ? accessorHeaderEndColumn + 1
                                : line.Length;
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "property",
                                Name = accessorName,
                                Line = accessorStartLine,
                                StartLine = accessorStartLine,
                                EndLine = accessorStartLine,
                                BodyStartLine = null,
                                BodyEndLine = null,
                                Signature = line[column..accessorSignatureEnd].Trim(),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = accessorVisibility,
                                ReturnType = NormalizeMetadata(
                                    accessorTypeStartColumn >= 0 && accessorTypeEndColumn >= accessorTypeStartColumn
                                        ? line[(accessorTypeStartColumn + 1)..(accessorTypeEndColumn + 1)]
                                        : null),
                            });
                        }

                        if (accessorHasInitializer)
                        {
                            inFieldInitializer = true;
                            initializerParenDepth = 0;
                            initializerBracketDepth = 0;
                            initializerBraceDepth = 0;
                        }

                        column = accessorHeaderEndColumn + 1;
                        continue;
                    }

                    var requireAbstractModifier = classScanTarget.ContainerKind == "class";
                    if (TryParseJavaScriptTypeScriptMemberPropertyHeader(
                        sanitizedLine,
                        column,
                        lang,
                        requireAbstractModifier,
                        out var propertyName,
                        out var propertyVisibility,
                        out var propertyTypeStartColumn,
                        out var propertyTypeEndColumn,
                        out var propertyHeaderEndColumn))
                    {
                        var propertyStartLine = i + 1;
                        if (seenMethodStarts.Add((propertyStartLine, column)))
                        {
                            var propertySignatureEnd = propertyHeaderEndColumn < line.Length
                                ? propertyHeaderEndColumn + 1
                                : line.Length;
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "property",
                                Name = propertyName,
                                Line = propertyStartLine,
                                StartLine = propertyStartLine,
                                EndLine = propertyStartLine,
                                BodyStartLine = null,
                                BodyEndLine = null,
                                Signature = line[column..propertySignatureEnd].Trim(),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = propertyVisibility,
                                ReturnType = NormalizeMetadata(
                                    line[(propertyTypeStartColumn + 1)..(propertyTypeEndColumn + 1)]),
                            });
                        }

                        column = propertyHeaderEndColumn + 1;
                        continue;
                    }
                }

                if (nestedBraceDepth == 0
                    && IsJavaScriptTypeScriptMethodCandidateStart(sanitizedLine, column)
                    && !IsJavaScriptTypeScriptControlFlowHeader(sanitizedLine, column))
                {
                    if (TryCaptureJavaScriptTypeScriptMethodHeader(
                        lines,
                        i,
                        column,
                        scanEndExclusive,
                        sanitizedLine,
                        lexState,
                        lang,
                        out var methodCapture))
                    {
                        var methodHeader = methodCapture.HeaderInfo;
                        var startLine = i + 1;
                        if (seenMethodStarts.Add((startLine, column)))
                        {
                            var (endLine, bodyStartLine, bodyEndLine) = methodHeader.HasBody
                                ? ResolveRange(lines, i, BodyStyle.Brace, lang, column)
                                : (methodCapture.HeaderEndLineIndex + 1, null, null);
                            var sameLineMethodEndColumn = methodHeader.HasBody && bodyEndLine == startLine
                                ? FindJavaScriptSameLineBodyEndColumn(line, column, lang)
                                : methodCapture.HeaderEndLineIndex == i
                                    ? methodCapture.HeaderEndColumn
                                    : -1;
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "function",
                                Name = GetJavaScriptTypeScriptMethodNameFromSource(methodCapture.SourceHeader, 0) ?? methodHeader.Name,
                                Line = startLine,
                                StartLine = startLine,
                                EndLine = Math.Max(startLine, endLine),
                                BodyStartLine = bodyStartLine,
                                BodyEndLine = bodyEndLine,
                                Signature = BuildJavaScriptTypeScriptBareMethodSignature(
                                    lines,
                                    i,
                                    column,
                                    bodyEndLine,
                                    sameLineMethodEndColumn,
                                    methodCapture,
                                    lang),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = methodHeader.Visibility,
                                ReturnType = GetJavaScriptTypeScriptBareMethodReturnType(methodCapture.SourceHeader, methodHeader, lang),
                            });

                            if (sameLineMethodEndColumn >= column)
                            {
                                column = sameLineMethodEndColumn + 1;
                                continue;
                            }

                            if (methodHeader.HasBody && methodCapture.BodyStartLineIndex > i)
                            {
                                pendingBodyStartLineIndex = methodCapture.BodyStartLineIndex;
                                pendingBodyStartColumn = methodCapture.BodyStartColumn;
                                break;
                            }

                            if (methodCapture.HeaderEndLineIndex > i)
                            {
                                pendingHeaderEndLineIndex = methodCapture.HeaderEndLineIndex;
                                pendingHeaderEndColumn = methodCapture.HeaderEndColumn;
                                break;
                            }
                        }

                        if (methodHeader.HasBody
                            && methodCapture.BodyStartLineIndex == i
                            && methodCapture.BodyStartColumn >= 0
                            && methodCapture.BodyStartColumn < sanitizedLine.Length)
                        {
                            nestedBraceDepth += CountBraces(sanitizedLine[methodCapture.BodyStartColumn..]);
                            if (nestedBraceDepth < 0)
                                nestedBraceDepth = 0;
                            break;
                        }

                        column++;
                        continue;
                    }

                    // Fallback: class-field arrow function (`handleClick = () => { ... }`).
                    // The method-header parser rejects these because they have no method-style
                    // parameter list before the body; handle them with a dedicated arrow parser so
                    // they still surface as function symbols instead of being consumed by the
                    // field-initializer state machine.
                    // クラスフィールドのアロー関数 (`handleClick = () => { ... }`) のフォールバック。
                    // メソッドヘッダーパーサは body 直前に method 形式の引数リストが来ないことを理由に
                    // これを弾くため、専用パーサで処理してフィールド初期化子ステートに吸われる前に
                    // function シンボルとして emit する。
                    if (TryCaptureJavaScriptTypeScriptClassFieldArrow(
                        lines,
                        i,
                        column,
                        scanEndExclusive,
                        sanitizedLine,
                        lexState,
                        lang,
                        out var arrowCapture))
                    {
                        var arrowHeader = arrowCapture.HeaderInfo;
                        var arrowStartLine = i + 1;
                        var isExpressionBody = arrowHeader.ExpressionBodyEndColumn != null
                            && arrowCapture.BodyEndLineIndex != null
                            && arrowCapture.BodyEndColumn != null;
                        if (seenMethodStarts.Add((arrowStartLine, column)))
                        {
                            int arrowEndLine;
                            int? arrowBodyStartLine;
                            int? arrowBodyEndLine;
                            int arrowSameLineEndColumn;
                            if (isExpressionBody)
                            {
                                arrowBodyStartLine = arrowCapture.BodyStartLineIndex + 1;
                                arrowBodyEndLine = arrowCapture.BodyEndLineIndex!.Value + 1;
                                arrowEndLine = arrowBodyEndLine.Value;
                                arrowSameLineEndColumn = arrowBodyEndLine == arrowStartLine
                                    ? arrowCapture.BodyEndColumn!.Value
                                    : -1;
                            }
                            else
                            {
                                (arrowEndLine, arrowBodyStartLine, arrowBodyEndLine) = ResolveRange(
                                    lines, i, BodyStyle.Brace, lang, arrowCapture.BodyStartColumn);
                                arrowSameLineEndColumn = arrowBodyEndLine == arrowStartLine
                                    ? FindJavaScriptSameLineArrowBodyEndColumn(line, arrowCapture.BodyStartColumn)
                                    : -1;
                            }
                            symbols.Add(new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "function",
                                Name = arrowHeader.Name,
                                Line = arrowStartLine,
                                StartLine = arrowStartLine,
                                EndLine = Math.Max(arrowStartLine, arrowEndLine),
                                BodyStartLine = arrowBodyStartLine,
                                BodyEndLine = arrowBodyEndLine,
                                Signature = BuildJavaScriptTypeScriptClassFieldArrowSignature(
                                    lines,
                                    i,
                                    column,
                                    arrowBodyEndLine,
                                    arrowSameLineEndColumn,
                                    arrowCapture),
                                ContainerKind = classScanTarget.ContainerKind,
                                ContainerName = classScanTarget.ContainerName,
                                Visibility = arrowHeader.Visibility,
                                ReturnType = GetJavaScriptTypeScriptBareMethodReturnType(arrowCapture.SourceHeader, arrowHeader, lang),
                            });

                            if (arrowSameLineEndColumn >= column)
                            {
                                column = arrowSameLineEndColumn + 1;
                                continue;
                            }

                            if (isExpressionBody)
                            {
                                // Expression-body spanned multiple lines; resume scanning just
                                // after the terminating `;` using the header-end pending channel
                                // (which only skips columns up to the sentinel, never entire lines)
                                // so the next field declaration on a subsequent line is still scanned.
                                // 式本体が複数行にまたがった場合、pendingHeaderEndLineIndex / Column で
                                // 終端 `;` 直後から再開する。列単位のスキップしかしないため、直後の行に
                                // ある field 宣言 (`runInline = ...`) を取りこぼさない。
                                pendingHeaderEndLineIndex = arrowCapture.BodyEndLineIndex!.Value;
                                pendingHeaderEndColumn = arrowCapture.BodyEndColumn!.Value;
                                break;
                            }

                            if (arrowCapture.BodyStartLineIndex > i)
                            {
                                pendingBodyStartLineIndex = arrowCapture.BodyStartLineIndex;
                                pendingBodyStartColumn = arrowCapture.BodyStartColumn;
                                break;
                            }
                        }

                        if (!isExpressionBody
                            && arrowCapture.BodyStartLineIndex == i
                            && arrowCapture.BodyStartColumn >= 0
                            && arrowCapture.BodyStartColumn < sanitizedLine.Length)
                        {
                            nestedBraceDepth += CountBraces(sanitizedLine[arrowCapture.BodyStartColumn..]);
                            if (nestedBraceDepth < 0)
                                nestedBraceDepth = 0;
                            break;
                        }

                        column++;
                        continue;
                    }
                }

                if (nestedBraceDepth == 0 && CanStartJavaScriptTypeScriptClassFieldInitializer(sanitizedLine, column))
                {
                    inFieldInitializer = true;
                    initializerParenDepth = 0;
                    initializerBracketDepth = 0;
                    initializerBraceDepth = 0;
                    column++;
                    continue;
                }

                if (ch == '{')
                {
                    nestedBraceDepth++;
                }
                else if (ch == '}' && nestedBraceDepth > 0)
                {
                    nestedBraceDepth--;
                }

                column++;
            }
        }
    }

    private static void AdvanceJavaScriptTypeScriptFieldInitializerState(
        ref bool inFieldInitializer,
        ref int initializerParenDepth,
        ref int initializerBracketDepth,
        ref int initializerBraceDepth,
        char ch)
    {
        if (ch == '(')
        {
            initializerParenDepth++;
            return;
        }

        if (ch == ')' && initializerParenDepth > 0)
        {
            initializerParenDepth--;
            return;
        }

        if (ch == '[')
        {
            initializerBracketDepth++;
            return;
        }

        if (ch == ']' && initializerBracketDepth > 0)
        {
            initializerBracketDepth--;
            return;
        }

        if (ch == '{')
        {
            initializerBraceDepth++;
            return;
        }

        if (ch == '}' && initializerBraceDepth > 0)
        {
            initializerBraceDepth--;
            return;
        }

        if (ch == ';'
            && initializerParenDepth == 0
            && initializerBracketDepth == 0
            && initializerBraceDepth == 0)
        {
            inFieldInitializer = false;
        }
    }

    private static bool StartsJavaScriptTypeScriptFieldInitializerContinuation(string continuationInput, string? lang)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < continuationInput.Length && char.IsWhiteSpace(continuationInput[firstNonWhitespace]))
            firstNonWhitespace++;

        if (firstNonWhitespace >= continuationInput.Length)
            return false;

        if (IsJavaScriptTypeScriptMethodCandidateStart(continuationInput, firstNonWhitespace))
        {
            var matchCandidate = lang == "typescript"
                ? NormalizeTypeScriptBareMethodMatchInput(continuationInput[firstNonWhitespace..])
                : continuationInput[firstNonWhitespace..];
            if (TryParseJavaScriptTypeScriptMethodHeader(matchCandidate, 0, lang, out _))
                return false;
        }

        return StartsJavaScriptTypeScriptExpressionContinuation(continuationInput);
    }

    private static bool CanStartJavaScriptTypeScriptClassFieldInitializer(string sanitizedLine, int index)
    {
        if (index < 0 || index >= sanitizedLine.Length || sanitizedLine[index] != '=')
            return false;

        return index + 1 >= sanitizedLine.Length || sanitizedLine[index + 1] != '>';
    }

    private static bool IsJavaScriptTypeScriptMethodCandidateStart(string sanitizedLine, int index)
    {
        if (index < 0 || index >= sanitizedLine.Length)
            return false;

        var ch = sanitizedLine[index];
        if (ch != '#'
            && ch != '@'
            && ch != '*'
            && ch != '['
            && ch != '\''
            && ch != '"'
            && !char.IsDigit(ch)
            && !IsJavaScriptTypeScriptIdentifierStart(ch))
            return false;

        return index == 0 || !IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index - 1]);
    }

    private static JavaScriptLexedLine LexJavaScriptLine(string line, JavaScriptLexState state)
    {
        var sanitized = new char[line.Length];
        var i = 0;

        while (i < line.Length)
        {
            var ch = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (state.Mode == JavaScriptLexMode.BlockComment)
            {
                sanitized[i] = ' ';
                if (ch == '*' && next == '/')
                {
                    sanitized[i + 1] = ' ';
                    state = state with { Mode = JavaScriptLexMode.Code };
                    i++;
                }

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.SingleQuote)
            {
                sanitized[i] = ch is '\'' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '\'')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.DoubleQuote)
            {
                sanitized[i] = ch is '"' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '"')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == JavaScriptLexMode.TemplateString)
            {
                sanitized[i] = ch is '`' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '`')
                    state = state with { Mode = JavaScriptLexMode.Code };

                i++;
                continue;
            }

            if (ch == '/' && next == '/')
            {
                while (i < line.Length)
                {
                    sanitized[i] = ' ';
                    i++;
                }

                break;
            }

            if (ch == '/' && next == '*')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = ' ';
                state = state with { Mode = JavaScriptLexMode.BlockComment };
                i++;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                sanitized[i] = ch;
                i++;
                continue;
            }

            if (state.ExpectingControlFlowOpenParen && ch != '(')
                state = state with { ExpectingControlFlowOpenParen = false };

            if (state.RegexAllowedAfterControlFlowParen && ch != '/')
            {
                state = state with
                {
                    RegexAllowedAfterControlFlowParen = false
                };
            }

            if (ch == '\'')
            {
                sanitized[i] = ch;
                state = state with { Mode = JavaScriptLexMode.SingleQuote, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '"')
            {
                sanitized[i] = ch;
                state = state with { Mode = JavaScriptLexMode.DoubleQuote, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '`')
            {
                sanitized[i] = ch;
                state = state with { Mode = JavaScriptLexMode.TemplateString, EscapeNext = false };
                i++;
                continue;
            }

            if (ch == '/' && CanStartJavaScriptRegexLiteral(state))
            {
                sanitized[i] = ' ';
                i = SkipJavaScriptRegexLiteral(line, sanitized, i);
                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Other,
                    PreviousIdentifier = null
                };
                i++;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_' || ch == '$')
            {
                var tokenStart = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '$'))
                {
                    sanitized[i] = line[i];
                    i++;
                }

                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Identifier,
                    PreviousIdentifier = line[tokenStart..i],
                    ExpectingControlFlowOpenParen = IsJavaScriptControlFlowKeyword(line[tokenStart..i])
                };
                continue;
            }

            if (char.IsDigit(ch))
            {
                sanitized[i] = ch;
                i++;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '.'))
                {
                    sanitized[i] = line[i];
                    i++;
                }

                state = state with
                {
                    PreviousTokenKind = JavaScriptPrevTokenKind.Number,
                    PreviousIdentifier = null,
                    ExpectingControlFlowOpenParen = false
                };
                continue;
            }

            sanitized[i] = ch;
            if (!char.IsWhiteSpace(ch))
            {
                var controlFlowParenDepth = state.ControlFlowParenDepth;
                var regexAllowedAfterControlFlowParen = state.RegexAllowedAfterControlFlowParen;

                if (ch == '(')
                {
                    if (state.ExpectingControlFlowOpenParen)
                    {
                        controlFlowParenDepth = 1;
                        regexAllowedAfterControlFlowParen = false;
                    }
                    else if (controlFlowParenDepth > 0)
                    {
                        controlFlowParenDepth++;
                    }
                }
                else if (ch == ')' && controlFlowParenDepth > 0)
                {
                    controlFlowParenDepth--;
                    if (controlFlowParenDepth == 0)
                        regexAllowedAfterControlFlowParen = true;
                }

                state = state with
                {
                    PreviousTokenKind = ch switch
                    {
                        ')' => JavaScriptPrevTokenKind.CloseParen,
                        ']' => JavaScriptPrevTokenKind.CloseBracket,
                        '}' => JavaScriptPrevTokenKind.CloseBrace,
                        _ => JavaScriptPrevTokenKind.Other
                    },
                    PreviousIdentifier = null,
                    ExpectingControlFlowOpenParen = false,
                    ControlFlowParenDepth = controlFlowParenDepth,
                    RegexAllowedAfterControlFlowParen = regexAllowedAfterControlFlowParen
                };
            }

            i++;
        }

        return new JavaScriptLexedLine(new string(sanitized), state);
    }

    // Sanitize a contiguous block of C# source lines for cross-line structural
    // analysis (attribute boundaries, bracket depth). String / char / comment
    // content is blanked to spaces while preserving original line lengths, and
    // the lexer state (VerbatimString / RawString / BlockComment / ...) is
    // threaded across line boundaries so multi-line literals no longer leak
    // stray `[` / `]` / `"` characters into downstream parsers.
    // After `LexCSharpLine` sanitization, string delimiters themselves (`"`,
    // `'`, `\`) are also blanked so continuation lines (e.g. `]")] decl` closing
    // a verbatim string from the previous physical line) do not look like they
    // open a fresh string literal when the caller scans them line-by-line.
    // C# ソース行の塊を、横断的な構造解析（属性境界や bracket depth）向けに
    // sanitize する。文字列 / 文字 / コメント内容は空白で置換し元の行長を保持、
    // lexer state（VerbatimString / RawString / BlockComment など）を行をまたいで
    // 持ち越すことで、複数行リテラル由来の `[` / `]` / `"` が下流パーサへ漏れない。
    // `LexCSharpLine` による sanitize 後、文字列区切りそのもの（`"`, `'`, `\`）も
    // 空白化する。こうしないと、前行の verbatim 文字列を閉じる継続行
    // （例: `]")] decl`）が単独で走査された際に新たな文字列リテラル開始と

    private static bool CanStartJavaScriptRegexLiteral(JavaScriptLexState state)
    {
        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.None)
            return true;

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.Other)
            return true;

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.Identifier)
        {
            return IsJavaScriptRegexPrefixKeyword(state.PreviousIdentifier);
        }

        if (state.PreviousTokenKind == JavaScriptPrevTokenKind.CloseParen)
            return state.RegexAllowedAfterControlFlowParen;

        return false;
    }

    private static bool IsJavaScriptControlFlowKeyword(string identifier)
    {
        return identifier is "if" or "for" or "while" or "switch" or "catch" or "with";
    }

    private static bool IsJavaScriptRegexPrefixKeyword(string? identifier)
    {
        return identifier is
            "return" or "throw" or "case" or "delete" or "typeof" or "void" or "new" or
            "in" or "of" or "instanceof" or "yield" or "await" or "else" or "do" or "finally";
    }

    private static int SkipJavaScriptRegexLiteral(string line, char[] sanitized, int slashIndex)
    {
        var i = slashIndex + 1;
        var inCharacterClass = false;

        while (i < line.Length)
        {
            sanitized[i] = ' ';
            var ch = line[i];
            if (ch == '\\')
            {
                if (i + 1 < line.Length)
                {
                    sanitized[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                return i;
            }

            if (ch == '[')
            {
                inCharacterClass = true;
                i++;
                continue;
            }

            if (ch == ']' && inCharacterClass)
            {
                inCharacterClass = false;
                i++;
                continue;
            }

            if (ch == '/' && !inCharacterClass)
            {
                i++;
                while (i < line.Length && char.IsLetter(line[i]))
                {
                    sanitized[i] = ' ';
                    i++;
                }

                return i - 1;
            }

            i++;
        }

        return line.Length - 1;
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindJavaScriptBraceRange(string[] lines, int startIndex, string? lang, int startColumn = 0)
    {
        var depth = 0;
        var opened = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var pendingArrowBody = false;
        var arrowExpressionActive = false;
        var arrowExpressionParenDepth = 0;
        var arrowExpressionBracketDepth = 0;
        var arrowExpressionBraceDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        int? bodyStartLine = null;
        var lexState = new JavaScriptLexState();

        for (int i = startIndex; i < lines.Length; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var effectiveStartColumn = startColumn;
            if (i == startIndex
                && startColumn > 0
                && TryParseJavaScriptTypeScriptMethodHeader(lexedLine.SanitizedLine, startColumn, lang, out var methodHeader))
            {
                effectiveStartColumn = methodHeader.BodyStartColumn;
            }

            var scanLine = i == startIndex && effectiveStartColumn > 0 && effectiveStartColumn < lexedLine.SanitizedLine.Length
                ? lexedLine.SanitizedLine[effectiveStartColumn..]
                : i == startIndex && effectiveStartColumn >= lexedLine.SanitizedLine.Length
                    ? string.Empty
                    : lexedLine.SanitizedLine;

            if (arrowExpressionActive
                && arrowExpressionBraceDepth == 0
                && arrowExpressionParenDepth == 0
                && arrowExpressionBracketDepth == 0
                && !StartsJavaScriptTypeScriptExpressionContinuation(scanLine))
            {
                return (i, bodyStartLine ?? startIndex + 1, i);
            }

            for (int column = 0; column < scanLine.Length; column++)
            {
                var ch = scanLine[column];
                if (!opened
                    && !arrowExpressionActive
                    && !functionHeaderState.Active
                    && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < scanLine.Length && IsJavaScriptTypeScriptIdentifierPart(scanLine[tokenEnd]))
                        tokenEnd++;

                    if (scanLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                if (!opened && !arrowExpressionActive)
                {
                    var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                        ref functionHeaderState,
                        scanLine,
                        column,
                        lang ?? "javascript",
                        out var functionHeaderAdvanceColumns);
                    if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                    {
                        column += functionHeaderAdvanceColumns;
                        continue;
                    }
                }

                if (!opened && !arrowExpressionActive && i == startIndex && ch == '=' && column + 1 < scanLine.Length && scanLine[column + 1] == '>')
                {
                    pendingArrowBody = true;
                    column++;
                    continue;
                }

                if (pendingArrowBody)
                {
                    if (char.IsWhiteSpace(ch))
                        continue;

                    bodyStartLine ??= i + 1;
                    if (ch == '{')
                        pendingArrowBody = false;
                    else
                    {
                        arrowExpressionActive = true;
                        pendingArrowBody = false;
                    }
                }

                if (arrowExpressionActive)
                {
                    if (ch == '(')
                    {
                        arrowExpressionParenDepth++;
                        continue;
                    }

                    if (ch == ')' && arrowExpressionParenDepth > 0)
                    {
                        arrowExpressionParenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        arrowExpressionBracketDepth++;
                        continue;
                    }

                    if (ch == ']' && arrowExpressionBracketDepth > 0)
                    {
                        arrowExpressionBracketDepth--;
                        continue;
                    }

                    if (ch == '{')
                    {
                        arrowExpressionBraceDepth++;
                        continue;
                    }

                    if (ch == '}' && arrowExpressionBraceDepth > 0)
                    {
                        arrowExpressionBraceDepth--;
                        continue;
                    }

                    if (ch == ';'
                        && arrowExpressionParenDepth == 0
                        && arrowExpressionBracketDepth == 0
                        && arrowExpressionBraceDepth == 0)
                    {
                        return (i + 1, bodyStartLine ?? startIndex + 1, i + 1);
                    }

                    continue;
                }

                if (!opened)
                {
                    if (ch == '(')
                    {
                        parenDepth++;
                        continue;
                    }

                    if (ch == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        bracketDepth++;
                        continue;
                    }

                    if (ch == ']' && bracketDepth > 0)
                    {
                        bracketDepth--;
                        continue;
                    }

                    if (ch == '<')
                    {
                        if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                            angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }
                }

                if (ch == '{')
                {
                    if (!opened && (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0))
                        continue;

                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                        return (i + 1, bodyStartLine, i + 1);
                }
            }

            if (!opened
                && !arrowExpressionActive
                && !functionHeaderState.Active
                && parenDepth == 0
                && bracketDepth == 0
                && angleDepth == 0
                && scanLine.TrimEnd().EndsWith(';'))
                return (startIndex + 1, null, null);
        }

        if (arrowExpressionActive)
            return (lines.Length, bodyStartLine ?? startIndex + 1, lines.Length);

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    private static int FindJavaScriptBodyOpenBraceIndex(string[] lines, int startIndex, int bodyStartIndex, string? lang, int startColumn = 0)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        var lexState = new JavaScriptLexState();

        for (int i = startIndex; i <= bodyStartIndex; i++)
        {
            var lexedLine = LexJavaScriptLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            var initialColumn = i == startIndex ? Math.Max(0, startColumn) : 0;
            for (int column = initialColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
                {
                    var tokenStart = column;
                    var tokenEnd = column + 1;
                    while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                        tokenEnd++;

                    if (sanitizedLine[tokenStart..tokenEnd] == "function")
                    {
                        BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                        column = tokenEnd - 1;
                        continue;
                    }
                }

                var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                    ref functionHeaderState,
                    sanitizedLine,
                    column,
                    lang ?? "javascript",
                    out var functionHeaderAdvanceColumns);
                if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
                {
                    column += functionHeaderAdvanceColumns;
                    continue;
                }

                if (!char.IsWhiteSpace(ch))
                {
                    if (ch == '(')
                    {
                        parenDepth++;
                        continue;
                    }

                    if (ch == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                        continue;
                    }

                    if (ch == '[')
                    {
                        bracketDepth++;
                        continue;
                    }

                    if (ch == ']' && bracketDepth > 0)
                    {
                        bracketDepth--;
                        continue;
                    }

                    if (ch == '<')
                    {
                        if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                            angleDepth++;
                        continue;
                    }

                    if (ch == '>' && angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }
                }

                if (ch != '{')
                    continue;

                if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                    continue;

                return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptSameLineBodyEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        if (!TryParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out var methodHeader))
            return -1;

        return FindJavaScriptSameLineBraceBodyEndColumn(sanitizedLine, methodHeader.BodyStartColumn);
    }

    // Same-line body end finder for class-field arrow functions. The scanner already knows the
    // sanitized body-open column from the arrow capture, so we walk braces from that column
    // without re-parsing the header (which the method-header parser would reject).
    // クラスフィールドのアロー関数向けの同一行 body 終了列探索。スキャナが arrow capture の段階で
    // sanitized 上の body 開始列を把握しているので、ヘッダを再パースせずそこから brace を辿る。
    private static int FindJavaScriptSameLineArrowBodyEndColumn(string line, int bodyStartColumn)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        return FindJavaScriptSameLineBraceBodyEndColumn(sanitizedLine, bodyStartColumn);
    }

    private static int FindJavaScriptSameLineBraceBodyEndColumn(string sanitizedLine, int bodyStartColumn)
    {
        var depth = 0;
        var opened = false;

        for (int column = Math.Max(0, bodyStartColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptSymbolStartColumn(string line, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return 0;

        var startColumn = line.IndexOf(signature, StringComparison.Ordinal);
        return startColumn >= 0 ? startColumn : 0;
    }

    private static int FindJavaScriptTypeScriptSameLineBraceEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        var bodyStartColumn = FindJavaScriptTypeScriptBodyOpenBraceColumn(sanitizedLine, startColumn, lang);
        if (bodyStartColumn < 0)
            return -1;

        var depth = 0;
        var opened = false;

        for (int column = bodyStartColumn; column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (ch == '{')
            {
                depth++;
                opened = true;
            }
            else if (ch == '}' && opened)
            {
                depth--;
                if (depth == 0)
                    return column;
            }
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptBodyOpenBraceColumn(string sanitizedLine, int startColumn, string? lang)
    {
        if (TryParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out var methodHeader))
            return methodHeader.BodyStartColumn;

        var parenDepth = 0;
        var bracketDepth = 0;
        var angleDepth = 0;
        var functionHeaderState = new JavaScriptTypeScriptFunctionHeaderState();
        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (!functionHeaderState.Active && IsJavaScriptTypeScriptIdentifierStart(ch))
            {
                var tokenStart = column;
                var tokenEnd = column + 1;
                while (tokenEnd < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[tokenEnd]))
                    tokenEnd++;

                if (sanitizedLine[tokenStart..tokenEnd] == "function")
                {
                    BeginJavaScriptTypeScriptFunctionHeader(ref functionHeaderState);
                    column = tokenEnd - 1;
                    continue;
                }
            }

            var functionHeaderResult = ConsumeJavaScriptTypeScriptFunctionHeaderChar(
                ref functionHeaderState,
                sanitizedLine,
                column,
                lang ?? "javascript",
                out var functionHeaderAdvanceColumns);
            if (functionHeaderResult == JavaScriptTypeScriptFunctionHeaderConsumeResult.Consumed)
            {
                column += functionHeaderAdvanceColumns;
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
                if (ch == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (ch == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                    continue;
                }

                if (ch == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                    continue;
                }

                if (ch == '<')
                {
                    if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                        angleDepth++;
                    continue;
                }

                if (ch == '>' && angleDepth > 0)
                {
                    angleDepth--;
                    continue;
                }
            }

            if (ch != '{')
                continue;

            if (parenDepth > 0 || bracketDepth > 0 || angleDepth > 0)
                continue;

            return column;
        }

        return -1;
    }

    private static int FindJavaScriptTypeScriptSameLineStatementEndColumn(string line, int startColumn, string? lang)
    {
        var sanitizedLine = LexJavaScriptLine(line, new JavaScriptLexState()).SanitizedLine;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        for (int column = Math.Max(0, startColumn); column < sanitizedLine.Length; column++)
        {
            var ch = sanitizedLine[column];
            if (char.IsWhiteSpace(ch))
                continue;

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
                case '<':
                    if (lang == "typescript" && parenDepth == 0 && bracketDepth == 0)
                        angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case ';':
                    if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                        return column;
                    break;
            }
        }

        return -1;
    }

    private static string NormalizeTypeScriptBareMethodMatchInput(string input)
    {
        if (!input.Contains('<', StringComparison.Ordinal) && !input.Contains('{', StringComparison.Ordinal))
            return input;

        if (!TryParseJavaScriptTypeScriptMethodHeader(input, 0, "typescript", out var methodHeader))
            return input;

        var chars = input.ToCharArray();
        if (methodHeader.GenericStartColumn != null && methodHeader.GenericEndColumn != null)
        {
            for (int replaceIndex = methodHeader.GenericStartColumn.Value; replaceIndex <= methodHeader.GenericEndColumn.Value; replaceIndex++)
                chars[replaceIndex] = ' ';
        }

        if (methodHeader.ReturnTypeStartColumn != null && methodHeader.ReturnTypeEndColumn != null)
        {
            for (int replaceIndex = methodHeader.ReturnTypeStartColumn.Value; replaceIndex <= methodHeader.ReturnTypeEndColumn.Value; replaceIndex++)
            {
                if (chars[replaceIndex] == '{')
                    chars[replaceIndex] = '(';
                else if (chars[replaceIndex] == '}')
                    chars[replaceIndex] = ')';
            }
        }

        return new string(chars);
    }

    // Class-field arrow like `handleClick = () => { ... }` is not matched by the method-header
    // parser because the identifier is followed by `=` instead of `(`. This parser handles that
    // shape (with optional TS modifiers, field type annotation, generics, and return type).
    // 正規表現や method-header パーサは `name = ... =>` 形式のクラスフィールド矢印関数を拾えないため、
    // 専用パーサでそのシェイプだけ（修飾子・フィールド型注釈・ジェネリクス・戻り値型を含む）をパースする。
    private static bool TryParseJavaScriptTypeScriptClassFieldArrowHeader(
        string sanitizedHeader,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderInfo arrowInfo)
    {
        arrowInfo = default;
        var index = Math.Max(0, startColumn);
        string? visibility = null;

        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(sanitizedHeader, ref index);

        string? candidateName = null;
        while (index < sanitizedHeader.Length)
        {
            if (!TryReadJavaScriptTypeScriptMethodToken(sanitizedHeader, ref index, out var token))
                return false;

            if (token == "*")
                return false;

            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;

            if (TypeScriptBareMethodModifiers.Contains(token)
                && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(sanitizedHeader, index))
            {
                // `get`/`set`/`async`/`abstract` as leading modifier here would turn the construct
                // back into a method (not an arrow field); bail so the method-header parser owns it.
                // `get`/`set`/`async`/`abstract` が先頭修飾子に来るケースは arrow field ではなく
                // method なので、method-header パーサ側に委ねるためここで諦める。
                if (token is "get" or "set" or "async" or "abstract")
                    return false;
                if (token is "public" or "private" or "protected")
                    visibility = token;
                continue;
            }

            candidateName = token;
            break;
        }

        if (candidateName == null)
            return false;

        if (index < sanitizedHeader.Length && (sanitizedHeader[index] == '?' || sanitizedHeader[index] == '!'))
        {
            index++;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == ':')
        {
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilFieldEquals(sanitizedHeader, ref index))
                return false;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != '=')
            return false;
        if (index + 1 < sanitizedHeader.Length && (sanitizedHeader[index + 1] == '=' || sanitizedHeader[index + 1] == '>'))
            return false;
        index++;
        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        if (index + 5 <= sanitizedHeader.Length
            && string.CompareOrdinal(sanitizedHeader, index, "async", 0, 5) == 0
            && (index + 5 == sanitizedHeader.Length || !IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[index + 5])))
        {
            index += 5;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        int? genericStartColumn = null;
        int? genericEndColumn = null;
        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == '<')
        {
            genericStartColumn = index;
            var angleDepth = 0;
            while (index < sanitizedHeader.Length)
            {
                var ch = sanitizedHeader[index];
                if (ch == '<')
                {
                    angleDepth++;
                }
                else if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
                {
                    index += 2;
                    continue;
                }
                else if (ch == '>')
                {
                    angleDepth--;
                    if (angleDepth == 0)
                    {
                        genericEndColumn = index;
                        index++;
                        break;
                    }
                }
                index++;
            }
            if (genericEndColumn == null)
                return false;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index >= sanitizedHeader.Length)
            return false;

        if (sanitizedHeader[index] == '(')
        {
            var parenDepth = 0;
            while (index < sanitizedHeader.Length)
            {
                var ch = sanitizedHeader[index];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        index++;
                        break;
                    }
                }
                index++;
            }
            if (parenDepth != 0)
                return false;
        }
        else if (IsJavaScriptTypeScriptIdentifierStart(sanitizedHeader[index]))
        {
            index++;
            while (index < sanitizedHeader.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[index]))
                index++;
        }
        else
        {
            return false;
        }

        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        int? returnTypeStartColumn = null;
        int? returnTypeEndColumn = null;
        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == ':')
        {
            returnTypeStartColumn = index;
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilArrow(sanitizedHeader, ref index, out var rtEnd))
                return false;
            returnTypeEndColumn = rtEnd;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index + 1 >= sanitizedHeader.Length
            || sanitizedHeader[index] != '='
            || sanitizedHeader[index + 1] != '>')
            return false;

        index += 2;
        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        if (index >= sanitizedHeader.Length)
            return false;

        // Block-body arrow (`=> { ... }`). HeaderEndColumn == BodyStartColumn, both point at `{`.
        // ブロック本体矢印 (`=> { ... }`)。header 終端と body 開始は同じ `{` を指す。
        if (sanitizedHeader[index] == '{')
        {
            arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                candidateName,
                index,
                visibility,
                genericStartColumn,
                genericEndColumn,
                returnTypeStartColumn,
                returnTypeEndColumn,
                index);
            return true;
        }

        // Expression-body arrow (`=> expr;`). Walk until a class-field terminator at depth 0.
        // Explicit `;` always terminates; implicit ASI also terminates when we hit the enclosing
        // class body `}` or a newline followed by a new class-member start (identifier+`=`/`(`,
        // `#private`, `*name`, decorator, or modifier keyword). `[` is treated as continuation
        // here because a bare `[` is ambiguous between computed-member access and a computed
        // method name; see StartsJavaScriptTypeScriptClassMemberAt for the full rationale.
        // `{}` / `()` / `[]` stay balanced; strings / comments are already masked by the upstream
        // lexer. If the accumulated header ends at depth 0 with expression tokens but no visible
        // terminator, return false so TryCapture pulls another line and retries.
        // 式本体矢印 (`=> expr;`)。深さ 0 でのクラスフィールド終端まで歩く。明示的な `;` は常に終端し、
        // 暗黙の ASI は囲みクラス body の `}` か、改行直後に新しいクラスメンバの開始 (identifier+`=`/`(`、
        // `#private`、`*name`、decorator、修飾子キーワード) が来た場合にも終端する。`[` は computed
        // member access の継続と computed method 名の両方になり得るためここでは継続扱いとする
        // (詳細は StartsJavaScriptTypeScriptClassMemberAt のコメント参照)。
        // 括弧類はバランスを取り、文字列・コメントは上流の lexer でマスク済み。終端が見えないまま
        // 蓄積ヘッダの末尾に達したら false を返し、TryCapture に次の行を積ませる。
        var expressionStart = index;
        var parenDepth2 = 0;
        var bracketDepth2 = 0;
        var braceDepth2 = 0;
        int? lastNonWhitespace = null;
        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == ';' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0)
            {
                if (lastNonWhitespace == null)
                    return false;
                arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                    candidateName,
                    expressionStart,
                    visibility,
                    genericStartColumn,
                    genericEndColumn,
                    returnTypeStartColumn,
                    returnTypeEndColumn,
                    expressionStart,
                    HasBody: true,
                    ExpressionBodyEndColumn: lastNonWhitespace);
                return true;
            }

            if (ch == '}' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0)
            {
                // Enclosing class body `}` at depth 0. If we already have expression tokens that
                // can validly end a statement (identifier/number/`)`/`]`/`}`), treat it as ASI and
                // emit. Otherwise bail so the class scanner handles the closer.
                // 囲みクラス body の `}` (深さ 0)。識別子/数値/`)`/`]`/`}` のように文末になり得るトークンが
                // 既に見えていれば ASI として終端扱いで emit する。無ければクラススキャナに委ねるため false。
                if (lastNonWhitespace != null
                    && CanJavaScriptTypeScriptExpressionEndAt(sanitizedHeader[lastNonWhitespace.Value]))
                {
                    arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                        candidateName,
                        expressionStart,
                        visibility,
                        genericStartColumn,
                        genericEndColumn,
                        returnTypeStartColumn,
                        returnTypeEndColumn,
                        expressionStart,
                        HasBody: true,
                        ExpressionBodyEndColumn: lastNonWhitespace);
                    return true;
                }
                return false;
            }

            if (ch == '\n' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0
                && lastNonWhitespace != null
                && CanJavaScriptTypeScriptExpressionEndAt(sanitizedHeader[lastNonWhitespace.Value]))
            {
                var peek = index + 1;
                while (peek < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[peek]))
                    peek++;
                // peek == sanitizedHeader.Length means we exhausted the accumulated header after
                // this newline — need more input from TryCapture. Break out of the heuristic and
                // fall through to the normal end-of-input `return false` path.
                // peek が末尾に達した場合は、この改行以降に蓄積ヘッダ上の文字が尽きたということなので
                // TryCapture に次の行を積ませる必要がある。ヒューリスティックは停止し、ループ末尾の
                // end-of-input `return false` に任せる。
                if (peek < sanitizedHeader.Length
                    && StartsJavaScriptTypeScriptClassMemberAt(sanitizedHeader, peek))
                {
                    arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                        candidateName,
                        expressionStart,
                        visibility,
                        genericStartColumn,
                        genericEndColumn,
                        returnTypeStartColumn,
                        returnTypeEndColumn,
                        expressionStart,
                        HasBody: true,
                        ExpressionBodyEndColumn: lastNonWhitespace);
                    return true;
                }
            }

            if (ch == '(') parenDepth2++;
            else if (ch == ')' && parenDepth2 > 0) parenDepth2--;
            else if (ch == '[') bracketDepth2++;
            else if (ch == ']' && bracketDepth2 > 0) bracketDepth2--;
            else if (ch == '{') braceDepth2++;
            else if (ch == '}' && braceDepth2 > 0) braceDepth2--;

            if (!char.IsWhiteSpace(ch))
                lastNonWhitespace = index;
            index++;
        }

        return false;
    }

    // Returns true when `ch` is a token that can validly end a JavaScript / TypeScript expression
    // (identifier/digit tail, closing bracket, `$`/`_`, or the closing delimiter of a string /
    // template literal). The upstream lexer preserves the opening and closing `"`/`'`/`` ` `` in
    // the sanitized header (only the body content is blanked to spaces), so a string-returning
    // arrow such as `only = () => "x"` ends with a visible quote character here.
    // Operator-like characters (`+`, `.`, `,`, etc.) return false so multi-line expression
    // continuations are not accidentally cut off by the ASI heuristic.
    // `ch` が JavaScript / TypeScript の式を終端できるトークン (識別子/数字末尾、閉じ括弧、`$`/`_`、
    // 文字列・テンプレートリテラルの閉じデリミタ) なら true。上流の lexer は sanitized header 上で
    // `"` / `'` / `` ` `` の開き/閉じ文字は残し、リテラル本体だけをスペースに blank する。
    // そのため `only = () => "x"` のような文字列を返す式は、ここでは閉じクォートが lastNonWhitespace と
    // して可視のまま残る。演算子類 (`+`、`.`、`,` 等) は false を返すことで、複数行の式継続が ASI
    // ヒューリスティックで誤って途中終端されないようにする。
    private static bool CanJavaScriptTypeScriptExpressionEndAt(char ch)
    {
        if (char.IsLetterOrDigit(ch))
            return true;
        return ch is '_' or '$' or ')' or ']' or '}' or '"' or '\'' or '`';
    }

    // Returns true when the position starts a new class-body member declaration: `}` (class body
    // close), `;` (stray empty statement), `#` / `@` / `*<name>` lead tokens, or an identifier that
    // is either a well-known class-member modifier keyword or is followed by a class-field /
    // method-shorthand syntactic marker (`=`, `(`, `<`, `?`, `!`, `:`, `;`).
    // Note: `[` is intentionally NOT a member-start signal here. A bare `[` after a newline is
    // ambiguous between a computed method name (`[Symbol.iterator]()`) and a computed member
    // access continuation (`foo\n  [bar]`). JavaScript's ASI rule explicitly forbids inserting a
    // `;` before a line that starts with `[`, so any source file that wants the computed-method
    // reading must write an explicit `;` — which the outer loop's `;` branch already handles. That
    // makes "treat `[` as continuation" the safe default for this heuristic.
    // Feed a sanitized (lex-masked) header string; strings/comments must already be blanked.
    // 指定位置がクラスボディの新しいメンバ宣言を始めるかを判定する: `}` (クラス body 閉じ)、
    // `;` (空文)、`#` / `@` / `*<name>` の先頭トークン、あるいは識別子で「クラスメンバ修飾キーワード」
    // または直後が `=` / `(` / `<` / `?` / `!` / `:` / `;` の場合。
    // 注意: `[` はあえて member-start として扱わない。改行直後の素の `[` は computed method name
    // (`[Symbol.iterator]()`) と computed member access の継続 (`foo\n  [bar]`) の両方に見えてしまう。
    // JavaScript の ASI 規則は `[` で始まる行の前に自動で `;` を挿入しないため、計算メンバ名を意図する
    // ソースは明示的に `;` を書く必要があり、そのケースは外側ループの `;` 分岐で既に拾える。よって
    // この ASI ヒューリスティックでは `[` を継続として扱うのが安全な既定。
    // 呼び出し側は lexer でマスク済み (文字列/コメントが blanked) の sanitizedHeader を渡すこと。
    private static bool StartsJavaScriptTypeScriptClassMemberAt(string sanitizedHeader, int index)
    {
        if (index < 0 || index >= sanitizedHeader.Length)
            return false;
        var ch = sanitizedHeader[index];
        if (ch is '}' or ';' or '#' or '@')
            return true;
        if (ch == '*')
        {
            var j = index + 1;
            while (j < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[j]))
                j++;
            if (j >= sanitizedHeader.Length)
                return false;
            var next = sanitizedHeader[j];
            return IsJavaScriptTypeScriptIdentifierStart(next) || next is '#' or '[';
        }
        if (!IsJavaScriptTypeScriptIdentifierStart(ch))
            return false;

        var end = index + 1;
        while (end < sanitizedHeader.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[end]))
            end++;
        var word = sanitizedHeader[index..end];
        if (word is "async" or "static" or "get" or "set" or "public" or "private" or "protected"
            or "readonly" or "override" or "abstract" or "declare" or "accessor" or "constructor")
        {
            return true;
        }

        var after = end;
        while (after < sanitizedHeader.Length && sanitizedHeader[after] != '\n' && char.IsWhiteSpace(sanitizedHeader[after]))
            after++;
        if (after >= sanitizedHeader.Length)
            return false;
        var follow = sanitizedHeader[after];
        return follow is '=' or '(' or '<' or '?' or '!' or ':' or ';';
    }

    // Walks a TypeScript type annotation starting at ':' through to the outer '=' that terminates
    // it (i.e., the class-field assignment operator). `=>` inside the type (arrow types) is
    // treated as a two-char token and skipped; `==` is likewise skipped so we do not terminate on
    // a stray comparison.
    // 型注釈 `:` から、フィールド代入の外側 `=` までを歩く。型内部の `=>` (arrow type) は 2 文字ひと組で
    // 読み飛ばし、`==` も比較演算子として読み飛ばして誤終端しないようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilFieldEquals(string sanitizedHeader, ref int index)
    {
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == '=')
                {
                    if (index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '=')
                    {
                        index += 2;
                        continue;
                    }
                    return true;
                }
                if (ch == ';' || ch == ',')
                    return false;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Walks a TypeScript member-property type annotation from `:` to the terminating `;`.
    // Arrow types inside nested parens / angles / brackets are skipped as two-char tokens so
    // `=>` in function types does not terminate the walk early.
    // TypeScript の member-property 型注釈を `:` から終端 `;` まで歩く。入れ子の
    // 括弧 / 山括弧 / 角括弧内の arrow type は 2 文字トークンとして読み飛ばし、
    // function type 内の `=>` で早期終了しないようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilSemicolon(string sanitizedHeader, ref int index, out int typeEndColumn)
    {
        typeEndColumn = -1;
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                    lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == ';')
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }
                if (!char.IsWhiteSpace(ch))
                    lastNonWs = index;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Walks a TypeScript return-type annotation from ':' to the terminating '=>'. Inner arrow
    // types inside parens/angles/brackets are skipped as two-char tokens without decrementing
    // depth. Returns the inclusive column of the last non-whitespace character of the type.
    // 戻り値型 `:` から最外殻の `=>` までを歩く。括弧/角括弧/山括弧内の arrow type は 2 文字単位で
    // 読み飛ばし深さを下げない。型末尾の非空白位置 (inclusive) を返す。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilArrow(
        string sanitizedHeader,
        ref int index,
        out int typeEndColumn)
    {
        typeEndColumn = -1;
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }
                lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            if (!char.IsWhiteSpace(ch))
                lastNonWs = index;
            index++;
        }

        return false;
    }

    private static bool TryParseJavaScriptTypeScriptMethodHeader(string sanitizedLine, int startColumn, string? lang, out JavaScriptTypeScriptMethodHeaderInfo methodHeader)
    {
        return ParseJavaScriptTypeScriptMethodHeader(sanitizedLine, startColumn, lang, out methodHeader)
            == JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
    }

    private static bool TryParseJavaScriptTypeScriptMemberPropertyHeader(
        string sanitizedLine,
        int startColumn,
        string? lang,
        bool requireAbstractModifier,
        out string name,
        out string? visibility,
        out int typeStartColumn,
        out int typeEndColumn,
        out int headerEndColumn)
    {
        name = string.Empty;
        visibility = null;
        typeStartColumn = -1;
        typeEndColumn = -1;
        headerEndColumn = -1;

        if (lang != "typescript")
            return false;

        var index = Math.Max(0, startColumn);
        var sawAbstract = false;
        var sawAccessor = false;
        var sawName = false;

        while (index < sanitizedLine.Length)
        {
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (index >= sanitizedLine.Length)
                return false;

            if (!TryReadJavaScriptTypeScriptSourceMethodName(sanitizedLine, ref index, out var token))
                return false;

            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (token is "public" or "private" or "protected")
            {
                visibility = token;
                continue;
            }

            if (token is "static" or "readonly" or "override" or "declare" or "accessor")
            {
                continue;
            }

            if (token == "abstract")
            {
                sawAbstract = true;
                continue;
            }

            if (token == "accessor")
            {
                sawAccessor = true;
                continue;
            }

            if (!IsJavaScriptTypeScriptIdentifierStart(token[0]))
                return false;

            name = token;
            sawName = true;
            break;
        }

        if (!sawName)
            return false;

        if (requireAbstractModifier && !sawAbstract && !sawAccessor)
            return false;

        if (index < sanitizedLine.Length && sanitizedLine[index] == '?')
        {
            index++;
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;
        }

        if (index >= sanitizedLine.Length || sanitizedLine[index] != ':')
            return false;

        typeStartColumn = index;
        if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilSemicolon(sanitizedLine, ref index, out typeEndColumn))
            return false;

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        if (index >= sanitizedLine.Length || sanitizedLine[index] != ';')
            return false;

        headerEndColumn = index;
        return true;
    }

    private static JavaScriptTypeScriptMethodHeaderParseStatus ParseJavaScriptTypeScriptMethodHeader(string sanitizedLine, int startColumn, string? lang, out JavaScriptTypeScriptMethodHeaderInfo methodHeader)
    {
        methodHeader = default;
        var index = Math.Max(0, startColumn);
        string? visibility = null;

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(sanitizedLine, ref index);

        while (index < sanitizedLine.Length)
        {
            while (true)
            {
                if (!TryReadJavaScriptTypeScriptMethodToken(sanitizedLine, ref index, out var token))
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                    index++;

                if (TypeScriptBareMethodModifiers.Contains(token)
                    && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(sanitizedLine, index))
                {
                    if (token is "public" or "private" or "protected")
                        visibility = token;
                    continue;
                }

                var isGenerator = token == "*";
                if (!isGenerator && index < sanitizedLine.Length && sanitizedLine[index] == '*')
                {
                    isGenerator = true;
                    index++;
                    while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                        index++;
                }

                if (isGenerator)
                {
                    if (!TryReadJavaScriptTypeScriptMethodName(sanitizedLine, ref index, out var generatorName))
                        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                    token = generatorName;
                }

                var name = token;

                int? genericStartColumn = null;
                int? genericEndColumn = null;
                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == '<')
                {
                    genericStartColumn = index;
                    var angleDepth = 0;
                    while (index < sanitizedLine.Length)
                    {
                        if (sanitizedLine[index] == '<')
                        {
                            angleDepth++;
                        }
                        else if (sanitizedLine[index] == '=' && index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '>')
                        {
                            index += 2;
                            continue;
                        }
                        else if (sanitizedLine[index] == '>')
                        {
                            angleDepth--;
                            if (angleDepth == 0)
                            {
                                genericEndColumn = index;
                                index++;
                                break;
                            }
                        }

                        index++;
                    }

                    if (genericEndColumn == null)
                        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                    while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                        index++;
                }

                if (index >= sanitizedLine.Length || sanitizedLine[index] != '(')
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                var parenDepth = 0;
                while (index < sanitizedLine.Length)
                {
                    if (sanitizedLine[index] == '(')
                    {
                        parenDepth++;
                    }
                    else if (sanitizedLine[index] == ')')
                    {
                        parenDepth--;
                        if (parenDepth == 0)
                        {
                            index++;
                            break;
                        }
                    }

                    index++;
                }

                if (parenDepth != 0)
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                    index++;

                int? returnTypeStartColumn = null;
                int? returnTypeEndColumn = null;
                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == ':')
                {
                    returnTypeStartColumn = index;
                    index++;
                    var returnParenDepth = 0;
                    var returnBracketDepth = 0;
                    var returnAngleDepth = 0;
                    var returnBraceDepth = 0;
                    var sawReturnTypeToken = false;
                    string? previousReturnToken = ":";

                    while (index < sanitizedLine.Length)
                    {
                        var ch = sanitizedLine[index];
                        if (char.IsWhiteSpace(ch))
                        {
                            index++;
                            continue;
                        }

                        if (ch == ';'
                            && returnParenDepth == 0
                            && returnBracketDepth == 0
                            && returnAngleDepth == 0
                            && returnBraceDepth == 0)
                        {
                            returnTypeEndColumn ??= index - 1;
                            methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, -1, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index, false);
                            return JavaScriptTypeScriptMethodHeaderParseStatus.DeclarationOnly;
                        }

                        if (ch == '(')
                        {
                            returnParenDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "(";
                            index++;
                            continue;
                        }

                        if (ch == ')' && returnParenDepth > 0)
                        {
                            returnParenDepth--;
                            previousReturnToken = ")";
                            index++;
                            continue;
                        }

                        if (ch == '[')
                        {
                            returnBracketDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "[";
                            index++;
                            continue;
                        }

                        if (ch == ']' && returnBracketDepth > 0)
                        {
                            returnBracketDepth--;
                            previousReturnToken = "]";
                            index++;
                            continue;
                        }

                        if (ch == '<')
                        {
                            returnAngleDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "<";
                            index++;
                            continue;
                        }

                        if (ch == '>' && returnAngleDepth > 0)
                        {
                            returnAngleDepth--;
                            previousReturnToken = ">";
                            index++;
                            continue;
                        }

                        if (ch == '{')
                        {
                            if (returnParenDepth == 0 && returnBracketDepth == 0 && returnAngleDepth == 0 && returnBraceDepth == 0)
                            {
                                if (CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(previousReturnToken))
                                {
                                    returnBraceDepth++;
                                    sawReturnTypeToken = true;
                                    previousReturnToken = "{";
                                    index++;
                                    continue;
                                }

                                if (sawReturnTypeToken)
                                {
                                    returnTypeEndColumn = index - 1;
                                    methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, index, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index);
                                    return JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
                                }
                            }

                            returnBraceDepth++;
                            sawReturnTypeToken = true;
                            previousReturnToken = "{";
                            index++;
                            continue;
                        }

                        if (ch == '}' && returnBraceDepth > 0)
                        {
                            returnBraceDepth--;
                            previousReturnToken = "}";
                            index++;
                            continue;
                        }

                        if (ch == '?' || ch == ':' || ch == '|' || ch == '&' || ch == ',')
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = ch.ToString();
                            index++;
                            continue;
                        }

                        if (ch == '=' && index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '>')
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = "=>";
                            index += 2;
                            continue;
                        }

                        if (TrySkipTypeScriptTypeToken(sanitizedLine, ref index, out var typeToken))
                        {
                            sawReturnTypeToken = true;
                            previousReturnToken = typeToken;
                            continue;
                        }

                        sawReturnTypeToken = true;
                        previousReturnToken = ch.ToString();
                        index++;
                    }

                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;
                }

                if (lang == "typescript" && index < sanitizedLine.Length && sanitizedLine[index] == ';')
                {
                    methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, -1, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index, false);
                    return JavaScriptTypeScriptMethodHeaderParseStatus.DeclarationOnly;
                }

                if (index >= sanitizedLine.Length || sanitizedLine[index] != '{')
                    return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;

                methodHeader = new JavaScriptTypeScriptMethodHeaderInfo(name, index, visibility, genericStartColumn, genericEndColumn, returnTypeStartColumn, returnTypeEndColumn, index);
                return JavaScriptTypeScriptMethodHeaderParseStatus.Parsed;
            }
        }

        return JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid;
    }

    private static bool TryCaptureJavaScriptTypeScriptMethodHeader(
        string[] lines,
        int startIndex,
        int startColumn,
        int scanEndExclusive,
        string firstSanitizedLine,
        JavaScriptLexState nextLineLexState,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture methodCapture)
    {
        methodCapture = default;
        var sourceBuilder = new System.Text.StringBuilder();
        var sanitizedBuilder = new System.Text.StringBuilder();

        // Content was split on '\n', so CRLF lines carry a trailing '\r'. Strip it from both
        // builders in lockstep so inter-line separators stay '\n' regardless of source line
        // endings; the sanitized lex output preserves '\r' at the same column as the source,
        // so dropping it from both keeps column mapping aligned (see #382 / #405).
        // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。sanitized 側も
        // source と同じ列に '\r' を保持するため、両方から一律に '\r' を落とせば column
        // mapping はズレず、行間セパレータも OS に依存せず '\n' に揃う（#382 / #405 参照）。
        var firstSourceSegmentRaw = startColumn < lines[startIndex].Length
            ? lines[startIndex][startColumn..]
            : string.Empty;
        var firstSanitizedSegmentRaw = startColumn < firstSanitizedLine.Length
            ? firstSanitizedLine[startColumn..]
            : string.Empty;
        var firstSourceSegment = StripTrailingCr(firstSourceSegmentRaw);
        var firstSanitizedSegment = StripTrailingCr(firstSanitizedSegmentRaw);
        sourceBuilder.Append(firstSourceSegment);
        sanitizedBuilder.Append(lang == "typescript"
            ? NormalizeTypeScriptBareMethodMatchInput(firstSanitizedSegment)
            : firstSanitizedSegment);

        if (TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
            sourceBuilder.ToString(),
            sanitizedBuilder.ToString(),
            startIndex,
            startColumn,
            lang,
            out methodCapture))
        {
            return true;
        }

        var lexState = nextLineLexState;
        for (int lineIndex = startIndex + 1; lineIndex < scanEndExclusive; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;

            var sourceLine = StripTrailingCr(lines[lineIndex]);
            var sanitizedLine = StripTrailingCr(lexedLine.SanitizedLine);
            sourceBuilder.Append('\n');
            sourceBuilder.Append(sourceLine);
            sanitizedBuilder.Append('\n');
            sanitizedBuilder.Append(lang == "typescript"
                ? NormalizeTypeScriptBareMethodMatchInput(sanitizedLine)
                : sanitizedLine);

            if (TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
                sourceBuilder.ToString(),
                sanitizedBuilder.ToString(),
                startIndex,
                startColumn,
                lang,
                out methodCapture))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseJavaScriptTypeScriptAccessorFieldHeader(
        string sanitizedLine,
        int startColumn,
        out string name,
        out string? visibility,
        out int typeStartColumn,
        out int typeEndColumn,
        out int headerEndColumn,
        out bool hasInitializer)
    {
        name = string.Empty;
        visibility = null;
        typeStartColumn = -1;
        typeEndColumn = -1;
        headerEndColumn = -1;
        hasInitializer = false;

        var index = Math.Max(0, startColumn);
        var sawAccessor = false;

        while (index < sanitizedLine.Length)
        {
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (index >= sanitizedLine.Length)
                return false;

            if (!TryReadJavaScriptTypeScriptSourceMethodName(sanitizedLine, ref index, out var token))
                return false;

            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;

            if (token is "public" or "private" or "protected")
            {
                visibility = token;
                continue;
            }

            if (token is "static" or "readonly" or "override" or "declare")
                continue;

            if (token == "accessor")
            {
                sawAccessor = true;
                continue;
            }

            if (!IsJavaScriptTypeScriptIdentifierStart(token[0]))
                return false;

            name = token;
            break;
        }

        if (!sawAccessor)
            return false;

        if (index < sanitizedLine.Length && sanitizedLine[index] == '?')
        {
            index++;
            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;
        }

        while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
            index++;

        if (index >= sanitizedLine.Length)
            return false;

        if (sanitizedLine[index] == ':')
        {
            typeStartColumn = index;
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilAccessorTerminator(sanitizedLine, ref index, out typeEndColumn, out hasInitializer))
                return false;

            while (index < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[index]))
                index++;
        }
        else if (sanitizedLine[index] == '=')
        {
            hasInitializer = true;
        }
        else
        {
            return false;
        }

        if (index >= sanitizedLine.Length)
            return false;

        if (sanitizedLine[index] == '=')
        {
            hasInitializer = true;
            headerEndColumn = index;
            return true;
        }

        if (sanitizedLine[index] != ';')
            return false;

        headerEndColumn = index;
        return true;
    }

    // Walks a TypeScript accessor field type annotation from `:` to either a terminating `;` or
    // a field initializer `=`. This mirrors the member-property helper but keeps the auto-accessor
    // initializer boundary visible so the outer scanner can switch into field-initializer mode.
    // TypeScript の accessor field 型注釈を `:` から終端 `;` または initializer `=` まで歩く。
    // member-property 用 helper を踏襲しつつ、auto-accessor の initializer 境界を外側に見せることで、
    // 呼び出し側が field-initializer モードへ切り替えられるようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilAccessorTerminator(
        string sanitizedLine,
        ref int index,
        out int typeEndColumn,
        out bool hasInitializer)
    {
        typeEndColumn = -1;
        hasInitializer = false;

        if (index >= sanitizedLine.Length || sanitizedLine[index] != ':')
            return false;

        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedLine.Length)
        {
            var ch = sanitizedLine[index];

            if (ch == '=' && index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                    lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == ';')
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }

                if (ch == '=')
                {
                    if (index + 1 < sanitizedLine.Length && sanitizedLine[index + 1] == '=')
                    {
                        index += 2;
                        continue;
                    }

                    typeEndColumn = lastNonWs;
                    hasInitializer = true;
                    return true;
                }

                if (!char.IsWhiteSpace(ch))
                    lastNonWs = index;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Multi-line accumulating wrapper for class-field arrow functions. Mirrors
    // TryCaptureJavaScriptTypeScriptMethodHeader: accumulates sanitized/source lines, calls the
    // arrow-header parser on each accumulation step, and maps the sanitized body-open column back
    // to a source (lineIndex, column) pair. Returns a JavaScriptTypeScriptMethodHeaderCapture so
    // the scanner can emit an arrow field symbol with the same machinery as method headers.
    // クラスフィールドのアロー関数に対する複数行蓄積ラッパー。
    // TryCaptureJavaScriptTypeScriptMethodHeader と同じく sanitized/source を行単位で蓄積し、
    // 蓄積ごとにアローヘッダーパーサを呼び、sanitized 上の body 開始列を source の
    // (行, 列) に逆写像する。戻り値は JavaScriptTypeScriptMethodHeaderCapture を使い回すため、
    // 呼び出し元の emit 処理はメソッドヘッダーと同じフローで扱える。
    private static bool TryCaptureJavaScriptTypeScriptClassFieldArrow(
        string[] lines,
        int startIndex,
        int startColumn,
        int scanEndExclusive,
        string firstSanitizedLine,
        JavaScriptLexState nextLineLexState,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        arrowCapture = default;
        var sourceBuilder = new System.Text.StringBuilder();
        var sanitizedBuilder = new System.Text.StringBuilder();

        var firstSourceSegmentRaw = startColumn < lines[startIndex].Length
            ? lines[startIndex][startColumn..]
            : string.Empty;
        var firstSanitizedSegmentRaw = startColumn < firstSanitizedLine.Length
            ? firstSanitizedLine[startColumn..]
            : string.Empty;
        sourceBuilder.Append(StripTrailingCr(firstSourceSegmentRaw));
        sanitizedBuilder.Append(StripTrailingCr(firstSanitizedSegmentRaw));

        if (TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
            sourceBuilder.ToString(),
            sanitizedBuilder.ToString(),
            startIndex,
            startColumn,
            lang,
            out arrowCapture))
        {
            return true;
        }

        var lexState = nextLineLexState;
        for (int lineIndex = startIndex + 1; lineIndex < scanEndExclusive; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;

            sourceBuilder.Append('\n');
            sourceBuilder.Append(StripTrailingCr(lines[lineIndex]));
            sanitizedBuilder.Append('\n');
            sanitizedBuilder.Append(StripTrailingCr(lexedLine.SanitizedLine));

            if (TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
                sourceBuilder.ToString(),
                sanitizedBuilder.ToString(),
                startIndex,
                startColumn,
                lang,
                out arrowCapture))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFinalizeJavaScriptTypeScriptClassFieldArrowCapture(
        string sourceHeader,
        string sanitizedHeader,
        int startIndex,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        arrowCapture = default;
        if (!TryParseJavaScriptTypeScriptClassFieldArrowHeader(sanitizedHeader, 0, lang, out var arrowInfo))
            return false;

        if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            arrowInfo.BodyStartColumn,
            out var bodyStartLineIndex,
            out var bodyStartColumn))
        {
            return false;
        }

        int? bodyEndLineIndex = null;
        int? bodyEndColumn = null;
        if (arrowInfo.ExpressionBodyEndColumn is int expressionEnd)
        {
            if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
                sourceHeader,
                startIndex,
                startColumn,
                expressionEnd,
                out var expressionEndLineIndex,
                out var expressionEndColumn))
            {
                return false;
            }
            bodyEndLineIndex = expressionEndLineIndex;
            bodyEndColumn = expressionEndColumn;
        }

        // For brace-body arrow fields, header end == body start (both point at `{`). For
        // expression-body arrow fields, BodyStartColumn points at the first expression char
        // and BodyEndLineIndex/Column describe the last expression char before `;`.
        // block body 矢印 field は header end と body start が同じ `{` を指す。式本体矢印 field は
        // BodyStartColumn が式の先頭、BodyEndLineIndex/Column が `;` 直前の式末尾を指す。
        arrowCapture = new JavaScriptTypeScriptMethodHeaderCapture(
            sourceHeader,
            arrowInfo,
            bodyStartLineIndex,
            bodyStartColumn,
            bodyStartLineIndex,
            bodyStartColumn,
            bodyEndLineIndex,
            bodyEndColumn);
        return true;
    }

    private static bool TryFinalizeJavaScriptTypeScriptMethodHeaderCapture(
        string sourceHeader,
        string sanitizedHeader,
        int startIndex,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderCapture methodCapture)
    {
        methodCapture = default;
        var parseStatus = ParseJavaScriptTypeScriptMethodHeader(sanitizedHeader, 0, lang, out var methodHeader);
        if (parseStatus == JavaScriptTypeScriptMethodHeaderParseStatus.IncompleteOrInvalid)
            return false;

        var headerEndLocationColumn = methodHeader.HasBody
            ? methodHeader.BodyStartColumn
            : methodHeader.HeaderEndColumn ?? -1;
        if (!TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            headerEndLocationColumn,
            out var headerEndLineIndex,
            out var headerEndColumn))
        {
            return false;
        }

        var bodyStartLineIndex = -1;
        var bodyStartColumn = -1;
        if (methodHeader.HasBody && !TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
            sourceHeader,
            startIndex,
            startColumn,
            methodHeader.BodyStartColumn,
            out bodyStartLineIndex,
            out bodyStartColumn))
        {
            return false;
        }

        methodCapture = new JavaScriptTypeScriptMethodHeaderCapture(
            sourceHeader,
            methodHeader,
            headerEndLineIndex,
            headerEndColumn,
            bodyStartLineIndex,
            bodyStartColumn);
        return true;
    }

    private static bool TryMapJavaScriptTypeScriptHeaderColumnToSourceLocation(
        string sourceHeader,
        int startIndex,
        int startColumn,
        int headerColumn,
        out int lineIndex,
        out int column)
    {
        lineIndex = startIndex;
        column = startColumn;
        if (headerColumn < 0 || headerColumn >= sourceHeader.Length)
            return false;

        for (int i = 0; i < headerColumn; i++)
        {
            if (sourceHeader[i] == '\n')
            {
                lineIndex++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return true;
    }

    private static string BuildJavaScriptTypeScriptBareMethodSignature(
        string[] lines,
        int startIndex,
        int startColumn,
        int? bodyEndLine,
        int sameLineMethodEndColumn,
        JavaScriptTypeScriptMethodHeaderCapture methodCapture,
        string? lang)
    {
        if (!methodCapture.HeaderInfo.HasBody)
        {
            if (methodCapture.HeaderEndLineIndex == startIndex && methodCapture.HeaderEndColumn >= startColumn)
                return lines[startIndex][startColumn..(methodCapture.HeaderEndColumn + 1)].Trim();

            if (methodCapture.HeaderInfo.HeaderEndColumn != null
                && methodCapture.HeaderInfo.HeaderEndColumn.Value >= 0
                && methodCapture.HeaderInfo.HeaderEndColumn.Value < methodCapture.SourceHeader.Length)
            {
                return methodCapture.SourceHeader[..(methodCapture.HeaderInfo.HeaderEndColumn.Value + 1)].Trim();
            }

            return methodCapture.SourceHeader.Trim();
        }

        if (bodyEndLine == startIndex + 1 && sameLineMethodEndColumn >= startColumn)
            return lines[startIndex][startColumn..(sameLineMethodEndColumn + 1)].Trim();

        if (methodCapture.HeaderInfo.BodyStartColumn < 0
            || methodCapture.HeaderInfo.BodyStartColumn >= methodCapture.SourceHeader.Length)
        {
            return methodCapture.SourceHeader.Trim();
        }

        return methodCapture.SourceHeader[..(methodCapture.HeaderInfo.BodyStartColumn + 1)].Trim();
    }

    // Build a signature string for a class-field arrow function. Same shape as the method-header
    // signature builder (same-line bodies quote the source slice verbatim, multi-line bodies stop
    // at the '{' that opens the block body).
    // クラスフィールドのアロー関数向けのシグネチャ文字列を組み立てる。メソッドヘッダー版と同じ方針で、
    // 同一行 body は source をそのまま切り出し、複数行 body はブロック本体を開く '{' まで切り出す。
    private static string BuildJavaScriptTypeScriptClassFieldArrowSignature(
        string[] lines,
        int startIndex,
        int startColumn,
        int? bodyEndLine,
        int sameLineArrowEndColumn,
        JavaScriptTypeScriptMethodHeaderCapture arrowCapture)
    {
        if (bodyEndLine == startIndex + 1 && sameLineArrowEndColumn >= startColumn)
            return lines[startIndex][startColumn..(sameLineArrowEndColumn + 1)].Trim();

        // For expression-body arrow fields that span multiple lines, include the full source up
        // to and including the last expression char (before `;`) so the signature reflects the
        // whole `name = (args) => expr` shape.
        // 複数行にわたる式本体矢印 field では、`;` 直前の式末尾までをシグネチャに含めて
        // `name = (args) => expr` 全体が見えるようにする。
        if (arrowCapture.HeaderInfo.ExpressionBodyEndColumn is int expressionEnd
            && expressionEnd >= 0
            && expressionEnd + 1 <= arrowCapture.SourceHeader.Length)
        {
            return arrowCapture.SourceHeader[..(expressionEnd + 1)].Trim();
        }

        if (arrowCapture.HeaderInfo.BodyStartColumn < 0
            || arrowCapture.HeaderInfo.BodyStartColumn >= arrowCapture.SourceHeader.Length)
        {
            return arrowCapture.SourceHeader.Trim();
        }

        return arrowCapture.SourceHeader[..(arrowCapture.HeaderInfo.BodyStartColumn + 1)].Trim();
    }

    private static string? GetJavaScriptTypeScriptBareMethodReturnType(string sourceHeader, JavaScriptTypeScriptMethodHeaderInfo methodHeader, string? lang)
    {
        if (lang != "typescript"
            || methodHeader.ReturnTypeStartColumn == null
            || methodHeader.ReturnTypeEndColumn == null)
            return null;

        var returnTypeStartColumn = methodHeader.ReturnTypeStartColumn.Value + 1;
        var returnTypeEndColumn = methodHeader.ReturnTypeEndColumn.Value;
        if (returnTypeEndColumn < returnTypeStartColumn || returnTypeEndColumn >= sourceHeader.Length)
            return null;

        return NormalizeMetadata(sourceHeader[returnTypeStartColumn..(returnTypeEndColumn + 1)]);
    }

    private static bool TryGetJavaScriptTypeScriptNextToken(
        string[] lines,
        int startIndex,
        int startColumn,
        bool skipWrappingParens,
        out int tokenLineIndex,
        out int tokenStartColumn,
        out string? token)
    {
        tokenLineIndex = -1;
        tokenStartColumn = -1;
        token = null;

        var lexState = new JavaScriptLexState();
        for (int lineIndex = startIndex; lineIndex < lines.Length; lineIndex++)
        {
            var lexedLine = LexJavaScriptLine(lines[lineIndex], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var column = lineIndex == startIndex ? startColumn : 0;

            while (column < sanitizedLine.Length)
            {
                var ch = sanitizedLine[column];
                if (char.IsWhiteSpace(ch))
                {
                    column++;
                    continue;
                }

                if (skipWrappingParens && ch == '(')
                {
                    column++;
                    continue;
                }

                if (!IsJavaScriptTypeScriptIdentifierStart(ch))
                    return false;

                var tokenStart = column;
                column++;
                while (column < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[column]))
                    column++;

                tokenLineIndex = lineIndex;
                tokenStartColumn = tokenStart;
                token = sanitizedLine[tokenStart..column];
                return true;
            }
        }

        return false;
    }

    private static bool IsJavaScriptTypeScriptIdentifierStart(char ch) =>
        char.IsLetter(ch) || ch == '_' || ch == '$';

    private static bool IsJavaScriptTypeScriptIdentifierPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_' || ch == '$';

    private static bool TrySkipTypeScriptTypeToken(string sanitizedLine, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        return TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref index, out token)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref index, out token)
            || TryReadJavaScriptTypeScriptIdentifierToken(sanitizedLine, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptIdentifierToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || !IsJavaScriptTypeScriptIdentifierStart(input[index]))
            return false;

        var tokenStart = index;
        index++;
        while (index < input.Length && IsJavaScriptTypeScriptIdentifierPart(input[index]))
            index++;

        token = input[tokenStart..index];
        return true;
    }

    private static bool TryReadJavaScriptTypeScriptQuotedLiteralToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || input[index] is not ('\'' or '"' or '`'))
            return false;

        var probe = index;
        var delimiter = input[probe];
        var tokenStart = probe;
        var escapeNext = false;
        probe++;
        while (probe < input.Length)
        {
            var ch = input[probe];
            if (escapeNext)
            {
                escapeNext = false;
                probe++;
                continue;
            }

            if (ch == '\\')
            {
                escapeNext = true;
                probe++;
                continue;
            }

            if (ch == delimiter)
            {
                probe++;
                index = probe;
                token = input[tokenStart..index];
                return true;
            }

            probe++;
        }

        return false;
    }

    private static bool TryReadJavaScriptTypeScriptNumericLiteralToken(string input, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= input.Length || !char.IsDigit(input[index]))
            return false;

        var tokenStart = index;
        if (input[index] == '0' && index + 1 < input.Length && input[index + 1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            index += 2;
            while (index < input.Length && IsJavaScriptTypeScriptNumericLiteralPart(input[index], allowDecimalPoint: false))
                index++;
        }
        else
        {
            while (index < input.Length && IsJavaScriptTypeScriptNumericLiteralPart(input[index], allowDecimalPoint: true))
                index++;
        }

        token = input[tokenStart..index];
        return true;
    }

    private static bool IsJavaScriptTypeScriptNumericLiteralPart(char ch, bool allowDecimalPoint)
    {
        if (char.IsLetterOrDigit(ch) || ch == '_')
            return true;

        return allowDecimalPoint && ch == '.';
    }

    private static bool TryReadJavaScriptTypeScriptMethodToken(string sanitizedLine, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        if (sanitizedLine[index] == '*')
        {
            token = "*";
            index++;
            return true;
        }

        return TryReadJavaScriptTypeScriptMethodName(sanitizedLine, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptMethodName(string sanitizedLine, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= sanitizedLine.Length)
            return false;

        var tokenStart = index;
        if (TryReadJavaScriptTypeScriptQuotedLiteralToken(sanitizedLine, ref index, out name)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref index, out name))
        {
            return true;
        }

        if (sanitizedLine[index] == '[')
        {
            var bracketDepth = 0;
            while (index < sanitizedLine.Length)
            {
                if (sanitizedLine[index] == '[')
                    bracketDepth++;
                else if (sanitizedLine[index] == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        index++;
                        name = sanitizedLine[tokenStart..index];
                        return true;
                    }
                }

                index++;
            }

            return false;
        }

        if (sanitizedLine[index] == '#')
        {
            index++;
            if (index >= sanitizedLine.Length || !IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
                return false;
        }
        else if (!IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
        {
            return false;
        }

        index++;
        while (index < sanitizedLine.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index]))
            index++;

        name = sanitizedLine[tokenStart..index];
        return true;
    }

    private static bool TrySkipJavaScriptTypeScriptDecorators(string line, ref int index)
    {
        var skippedAny = false;

        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index >= line.Length || line[index] != '@')
                return skippedAny;

            skippedAny = true;
            index++;

            var parenDepth = 0;
            var bracketDepth = 0;
            var braceDepth = 0;

            while (index < line.Length)
            {
                var ch = line[index];
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && char.IsWhiteSpace(ch))
                    break;

                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == '{')
                    braceDepth++;
                else if (ch == '}' && braceDepth > 0)
                    braceDepth--;

                index++;
            }
        }

        return skippedAny;
    }

    private static string? GetJavaScriptTypeScriptMethodNameFromSource(string line, int startColumn)
    {
        var index = Math.Max(0, startColumn);
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(line, ref index);

        while (index < line.Length)
        {
            if (!TryReadJavaScriptTypeScriptSourceMethodToken(line, ref index, out var token))
                return null;

            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (TypeScriptBareMethodModifiers.Contains(token)
                && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(line, index))
            {
                continue;
            }

            var isGenerator = token == "*";
            if (!isGenerator && index < line.Length && line[index] == '*')
            {
                isGenerator = true;
                index++;
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                    index++;
            }

            if (isGenerator)
                return TryReadJavaScriptTypeScriptSourceMethodName(line, ref index, out var generatorName) ? generatorName : null;

            return token;
        }

        return null;
    }

    private static bool TryReadJavaScriptTypeScriptSourceMethodToken(string line, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= line.Length)
            return false;

        if (line[index] == '*')
        {
            token = "*";
            index++;
            return true;
        }

        return TryReadJavaScriptTypeScriptSourceMethodName(line, ref index, out token);
    }

    private static bool TryReadJavaScriptTypeScriptSourceQuotedLiteralToken(string line, ref int index, out string token)
    {
        token = string.Empty;
        if (index >= line.Length || line[index] is not ('\'' or '"' or '`'))
            return false;

        var delimiter = line[index];
        var tokenStart = index;
        var escapeNext = false;
        index++;
        while (index < line.Length)
        {
            var ch = line[index];
            if (escapeNext)
            {
                escapeNext = false;
                index++;
                continue;
            }

            if (ch == '\\')
            {
                escapeNext = true;
                index++;
                continue;
            }

            if (ch == delimiter)
            {
                index++;
                token = line[tokenStart..index];
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool TryReadJavaScriptTypeScriptSourceMethodName(string line, ref int index, out string name)
    {
        name = string.Empty;
        if (index >= line.Length)
            return false;

        var tokenStart = index;
        if (TryReadJavaScriptTypeScriptSourceQuotedLiteralToken(line, ref index, out name)
            || TryReadJavaScriptTypeScriptNumericLiteralToken(line, ref index, out name))
        {
            return true;
        }

        if (line[index] == '[')
        {
            var bracketDepth = 0;
            var inSingleQuote = false;
            var inDoubleQuote = false;
            var inTemplateString = false;
            var escapeNext = false;
            while (index < line.Length)
            {
                var ch = line[index];
                if (escapeNext)
                {
                    escapeNext = false;
                    index++;
                    continue;
                }

                if (inSingleQuote)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '\'')
                        inSingleQuote = false;
                    index++;
                    continue;
                }

                if (inDoubleQuote)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '"')
                        inDoubleQuote = false;
                    index++;
                    continue;
                }

                if (inTemplateString)
                {
                    if (ch == '\\')
                        escapeNext = true;
                    else if (ch == '`')
                        inTemplateString = false;
                    index++;
                    continue;
                }

                if (ch == '\'')
                {
                    inSingleQuote = true;
                    index++;
                    continue;
                }

                if (ch == '"')
                {
                    inDoubleQuote = true;
                    index++;
                    continue;
                }

                if (ch == '`')
                {
                    inTemplateString = true;
                    index++;
                    continue;
                }

                if (ch == '[')
                {
                    bracketDepth++;
                }
                else if (ch == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        index++;
                        name = line[tokenStart..index];
                        return true;
                    }
                }

                index++;
            }

            return false;
        }

        if (line[index] == '#')
        {
            index++;
            if (index >= line.Length || !IsJavaScriptTypeScriptIdentifierStart(line[index]))
                return false;
        }
        else if (!IsJavaScriptTypeScriptIdentifierStart(line[index]))
        {
            return false;
        }

        index++;
        while (index < line.Length && IsJavaScriptTypeScriptIdentifierPart(line[index]))
            index++;

        name = line[tokenStart..index];
        return true;
    }

    private static int FindNextJavaScriptTypeScriptTokenStart(string sanitizedLine, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < sanitizedLine.Length)
        {
            if (!IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[index]))
            {
                index++;
                continue;
            }

            if (index > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index - 1]))
            {
                index++;
                continue;
            }

            return index;
        }

        return -1;
    }

    private static int FindNextJavaScriptTypeScriptMethodCandidateStart(string sanitizedLine, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < sanitizedLine.Length)
        {
            var ch = sanitizedLine[index];
            if (ch != '#'
                && ch != '@'
                && ch != '*'
                && ch != '['
                && ch != '\''
                && ch != '"'
                && !char.IsDigit(ch)
                && !IsJavaScriptTypeScriptIdentifierStart(ch))
            {
                index++;
                continue;
            }

            if (index > 0 && IsJavaScriptTypeScriptIdentifierPart(sanitizedLine[index - 1]))
            {
                index++;
                continue;
            }

            return index;
        }

        return -1;
    }

    private static int FindNextJavaScriptTypeScriptStatementStart(string sanitizedLine, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < sanitizedLine.Length)
        {
            index = FindNextJavaScriptTypeScriptTokenStart(sanitizedLine, index);
            if (index < 0)
                return -1;

            var previous = index - 1;
            while (previous >= 0 && char.IsWhiteSpace(sanitizedLine[previous]))
                previous--;

            if (previous < 0 || sanitizedLine[previous] is ';' or '{' or '}')
                return index;

            index++;
        }

        return -1;
    }

    private static bool CanTreatJavaScriptTypeScriptMethodTokenAsModifier(string sanitizedLine, int index)
    {
        var lookahead = index;
        while (lookahead < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[lookahead]))
            lookahead++;

        if (lookahead >= sanitizedLine.Length)
            return false;

        var ch = sanitizedLine[lookahead];
        if (ch is '(' or '<')
            return false;

        if (ch == '*')
        {
            lookahead++;
            while (lookahead < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[lookahead]))
                lookahead++;

            if (lookahead >= sanitizedLine.Length)
                return false;

            return sanitizedLine[lookahead] is '[' or '#'
                || IsJavaScriptTypeScriptIdentifierStart(sanitizedLine[lookahead]);
        }

        return ch is '[' or '#'
            || IsJavaScriptTypeScriptIdentifierStart(ch);
    }

    private static bool CanStartJavaScriptTypeScriptReturnTypeObjectLiteral(string? previousReturnToken)
    {
        return previousReturnToken is ":" or "?" or "|" or "&" or "," or "(" or "[" or "=>" or "extends";
    }

}
