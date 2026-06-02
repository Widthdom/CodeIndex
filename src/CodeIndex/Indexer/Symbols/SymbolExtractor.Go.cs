using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static bool TryHandleGoBlockLine(
        long fileId,
        string line,
        int lineIndex,
        List<SymbolRecord> symbols,
        ref bool inImportBlock)
    {
        var trimmed = line.TrimStart();

        if (TryAddGoDirectiveSymbol(fileId, line, lineIndex, symbols, trimmed))
            return true;

        if (inImportBlock)
        {
            if (trimmed.Length == 0
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.StartsWith(")", StringComparison.Ordinal))
            {
                inImportBlock = false;
                return true;
            }

            var closingParenIndex = trimmed.IndexOf(')');
            if (closingParenIndex >= 0)
            {
                var blockImportText = trimmed[..closingParenIndex].TrimEnd();
                if (blockImportText.Length > 0)
                    TryAddGoImportSymbol(fileId, line, lineIndex, symbols, blockImportText);

                inImportBlock = false;
                return true;
            }

            return TryAddGoImportSymbol(fileId, line, lineIndex, symbols, trimmed);
        }

        if (trimmed.StartsWith("import", StringComparison.Ordinal))
        {
            var afterImport = trimmed["import".Length..].TrimStart();
            if (afterImport.StartsWith("(", StringComparison.Ordinal))
            {
                var blockRemainder = afterImport[1..].TrimStart();
                if (blockRemainder.Length > 0)
                {
                    var closingParenIndex = blockRemainder.IndexOf(')');
                    if (closingParenIndex >= 0)
                    {
                        var blockImportText = blockRemainder[..closingParenIndex].TrimEnd();
                        if (blockImportText.Length > 0)
                            TryAddGoImportSymbol(fileId, line, lineIndex, symbols, blockImportText);

                        inImportBlock = false;
                        return true;
                    }

                    TryAddGoImportSymbol(fileId, line, lineIndex, symbols, blockRemainder);
                }

                inImportBlock = true;
                return true;
            }

            return TryAddGoImportSymbol(fileId, line, lineIndex, symbols, afterImport);
        }

        return false;
    }

    private static bool TryAddGoTypeSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string typeText,
        ref int goTypeBodyDepth,
        ref string? goTypeBodyKind)
    {
        var normalizedTypeText = typeText.StartsWith("type", StringComparison.Ordinal)
            ? typeText["type".Length..].TrimStart()
            : typeText;
        var match = GoTypeBlockSpecRegex.Match(normalizedTypeText);
        if (!match.Success)
            return true;

        var name = match.Groups["name"].Value.Trim();
        var kind = Regex.IsMatch(normalizedTypeText, @"\bstruct\b")
            ? "struct"
            : Regex.IsMatch(normalizedTypeText, @"\binterface\b")
                ? "protocol"
                : "class";
        if (HasGoSymbol(symbols, fileId, lineIndex + 1, kind, name))
        {
            if (kind == "struct")
                TryAddGoStructEmbeddedTypeSymbols(fileId, rawLine, lineIndex, symbols, ExtractGoInlineTypeBody(typeText));

            return true;
        }
        var startColumn = rawLine.IndexOf(name, StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.Length - rawLine.TrimStart().Length;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = kind,
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = name,
            },
            rawLine);

        if (kind == "struct")
        {
            TryAddGoStructEmbeddedTypeSymbols(fileId, rawLine, lineIndex, symbols, ExtractGoInlineTypeBody(typeText));
            goTypeBodyDepth = CountGoBraceDelta(typeText);
            goTypeBodyKind = goTypeBodyDepth > 0 ? kind : null;
        }
        else if (kind == "protocol")
        {
            goTypeBodyDepth = CountGoBraceDelta(typeText);
            goTypeBodyKind = goTypeBodyDepth > 0 ? kind : null;
        }

        return true;
    }

    private static bool TryAddGoValueSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string valueText)
    {
        var match = GoValueBlockSpecRegex.Match(valueText);
        if (!match.Success)
            return true;

        foreach (var name in match.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name == "_")
                continue;

            if (HasGoSymbol(symbols, fileId, lineIndex + 1, "property", name))
                continue;

            var startColumn = rawLine.IndexOf(name, StringComparison.Ordinal);
            if (startColumn < 0)
                startColumn = rawLine.Length - rawLine.TrimStart().Length;

            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineIndex + 1,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = name,
                    Line = lineIndex + 1,
                    StartLine = lineIndex + 1,
                    StartColumn = startColumn,
                    EndLine = lineIndex + 1,
                    Signature = name,
                },
                rawLine);
        }

        return true;
    }

    private static bool TryAddGoLabelSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols)
    {
        var trimmed = rawLine.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith("*", StringComparison.Ordinal))
        {
            return false;
        }

        var match = GoLabelRegex.Match(rawLine);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value;
        if (IsGoLabelKeyword(name) || HasGoSymbol(symbols, fileId, lineIndex + 1, "function", name))
            return false;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = match.Groups["name"].Index,
                EndLine = lineIndex + 1,
                Signature = name,
            },
            rawLine);
        return true;
    }

    private static bool IsGoLabelKeyword(string name)
        => name is "break" or "case" or "chan" or "const" or "continue" or "default" or "defer"
            or "else" or "fallthrough" or "for" or "func" or "go" or "goto" or "if" or "import"
            or "interface" or "map" or "package" or "range" or "return" or "select" or "struct"
            or "switch" or "type" or "var";

    private static int CountGoBraceDelta(string text)
    {
        var delta = 0;
        foreach (var ch in text)
        {
            if (ch == '{')
                delta++;
            else if (ch == '}')
                delta--;
        }

        return delta;
    }

    private static int CountGoCodeBraceDelta(string text, ref bool inBlockComment, ref bool inRawString)
    {
        var delta = 0;
        var inString = false;
        var inRune = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inBlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (inRawString)
            {
                if (ch == '`')
                    inRawString = false;

                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;

                continue;
            }

            if (inRune)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '\'')
                    inRune = false;

                continue;
            }

            if (ch == '/' && next == '/')
                break;

            if (ch == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (ch == '`')
            {
                inRawString = true;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '\'')
            {
                inRune = true;
                continue;
            }

            if (ch == '{')
                delta++;
            else if (ch == '}')
                delta--;
        }

        return delta;
    }

    private static bool TryAddGoImportSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string importText)
    {
        var match = GoImportSpecRegex.Match(importText);
        if (!match.Success)
            return true;

        var name = match.Groups["name"].Value.Trim();
        var kind = string.Equals(name, @"""C""", StringComparison.Ordinal) ? "cgo" : "import";
        var startColumn = rawLine.IndexOf(name, StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.IndexOf(importText, StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.Length - rawLine.TrimStart().Length;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = kind,
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = name,
            },
            rawLine);
        return true;
    }

    private static bool TryAddGoDirectiveSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string trimmed)
    {
        if (!trimmed.StartsWith("//go:build", StringComparison.Ordinal)
            && !trimmed.StartsWith("//go:test", StringComparison.Ordinal))
        {
            return false;
        }

        var name = trimmed[2..].Trim();
        if (HasGoSymbol(symbols, fileId, lineIndex + 1, "annotation", name))
            return true;

        var startColumn = rawLine.IndexOf("//go:", StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.Length - rawLine.TrimStart().Length;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "annotation",
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = trimmed,
            },
            rawLine);
        return true;
    }

    private static void ExtractGoInterfaceMethods(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        var awaitingInterfaceBody = false;
        var inInterfaceBody = false;
        var interfaceBodyDepth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.TrimStart();

            if (!inInterfaceBody)
            {
                if (!awaitingInterfaceBody)
                {
                    if (!GoInterfaceHeaderRegex.IsMatch(trimmed))
                        continue;
                }
                else if (trimmed.Length == 0
                    || trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                var openBraceIndex = rawLine.IndexOf('{');
                if (openBraceIndex < 0)
                {
                    awaitingInterfaceBody = true;
                    continue;
                }

                inInterfaceBody = true;
                awaitingInterfaceBody = false;
                interfaceBodyDepth = CountGoBraceDelta(rawLine);

                var sameLineBody = rawLine[(openBraceIndex + 1)..].TrimStart();
                if (sameLineBody.Length > 0
                    && !sameLineBody.StartsWith("//", StringComparison.Ordinal)
                    && !sameLineBody.StartsWith("/*", StringComparison.Ordinal)
                    && !sameLineBody.StartsWith("}", StringComparison.Ordinal))
                {
                    TryAddGoInterfaceBodySymbols(fileId, rawLine, i, symbols, sameLineBody);
                }

                if (interfaceBodyDepth <= 0)
                {
                    inInterfaceBody = false;
                    interfaceBodyDepth = 0;
                }

                continue;
            }

            if (trimmed.Length == 0
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                interfaceBodyDepth += CountGoBraceDelta(rawLine);
                if (interfaceBodyDepth <= 0)
                {
                    inInterfaceBody = false;
                    interfaceBodyDepth = 0;
                }

                continue;
            }

            var candidate = trimmed;
            var trailingBraceIndex = candidate.IndexOf('}');
            if (trailingBraceIndex >= 0)
                candidate = candidate[..trailingBraceIndex].TrimEnd();

            if (candidate.Length > 0 && !candidate.StartsWith("}", StringComparison.Ordinal))
                TryAddGoInterfaceBodySymbols(fileId, rawLine, i, symbols, candidate);

            interfaceBodyDepth += CountGoBraceDelta(rawLine);
            if (interfaceBodyDepth <= 0)
            {
                inInterfaceBody = false;
                interfaceBodyDepth = 0;
            }
        }
    }

    private static void TryAddGoInterfaceBodySymbols(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string bodyText)
    {
        foreach (var segment in bodyText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = segment;
            var trailingBraceIndex = candidate.IndexOf('}');
            if (trailingBraceIndex >= 0)
                candidate = candidate[..trailingBraceIndex].TrimEnd();

            var lineCommentIndex = candidate.IndexOf("//", StringComparison.Ordinal);
            if (lineCommentIndex >= 0)
                candidate = candidate[..lineCommentIndex].TrimEnd();

            var blockCommentIndex = candidate.IndexOf("/*", StringComparison.Ordinal);
            if (blockCommentIndex >= 0)
                candidate = candidate[..blockCommentIndex].TrimEnd();

            if (candidate.Length == 0
                || candidate.StartsWith("//", StringComparison.Ordinal)
                || candidate.StartsWith("/*", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryAddGoInterfaceMethodSymbol(fileId, rawLine, lineIndex, symbols, candidate))
                continue;

            TryAddGoInterfaceEmbeddedTypeSymbol(fileId, rawLine, lineIndex, symbols, candidate);
        }
    }

    private static bool TryAddGoInterfaceMethodSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string candidate)
    {
        var match = GoInterfaceMethodRegex.Match(candidate);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value.Trim();
        if (HasGoSymbol(symbols, fileId, lineIndex + 1, "function", name))
            return true;

        var startColumn = rawLine.IndexOf(name, StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.Length - rawLine.TrimStart().Length;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "function",
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = candidate.Trim(),
            },
            rawLine);
        return true;
    }

    private static bool TryAddGoInterfaceEmbeddedTypeSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string candidate)
    {
        var match = GoInterfaceEmbeddedTypeRegex.Match(candidate);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value.Trim();
        if (name.Length == 0)
            return false;

        if (!name.Contains('.') && GoInterfaceEmbeddedTypeBlacklist.Contains(name))
        {
            return false;
        }

        if (HasGoSymbol(symbols, fileId, lineIndex + 1, "import", name))
            return true;

        var startColumn = rawLine.IndexOf(name, StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.Length - rawLine.TrimStart().Length;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "import",
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = rawLine.Trim(),
            },
            rawLine);
        return true;
    }

    private static string ExtractGoInlineTypeBody(string typeText)
    {
        var openBraceIndex = typeText.IndexOf('{');
        if (openBraceIndex < 0)
            return string.Empty;

        var body = typeText[(openBraceIndex + 1)..];
        var closeBraceIndex = body.LastIndexOf('}');
        return closeBraceIndex >= 0 ? body[..closeBraceIndex] : body;
    }

    private static void TryAddGoStructEmbeddedTypeSymbols(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return;

        foreach (var segment in bodyText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = segment;
            var trailingBraceIndex = candidate.IndexOf('}');
            if (trailingBraceIndex >= 0)
                candidate = candidate[..trailingBraceIndex].TrimEnd();

            var lineCommentIndex = candidate.IndexOf("//", StringComparison.Ordinal);
            if (lineCommentIndex >= 0)
                candidate = candidate[..lineCommentIndex].TrimEnd();

            var blockCommentIndex = candidate.IndexOf("/*", StringComparison.Ordinal);
            if (blockCommentIndex >= 0)
                candidate = candidate[..blockCommentIndex].TrimEnd();

            var tagIndex = candidate.IndexOf('`');
            if (tagIndex >= 0)
                candidate = candidate[..tagIndex].TrimEnd();

            if (candidate.Length == 0)
                continue;

            TryAddGoStructEmbeddedTypeSymbol(fileId, rawLine, lineIndex, symbols, candidate);
        }
    }

    private static bool TryAddGoStructEmbeddedTypeSymbol(
        long fileId,
        string rawLine,
        int lineIndex,
        List<SymbolRecord> symbols,
        string candidate)
    {
        var match = GoStructEmbeddedTypeRegex.Match(candidate);
        if (!match.Success)
            return false;

        var name = match.Groups["name"].Value.Trim();
        if (name.Length == 0 || HasGoSymbol(symbols, fileId, lineIndex + 1, "import", name))
            return true;

        var startColumn = rawLine.IndexOf(name, StringComparison.Ordinal);
        if (startColumn < 0)
            startColumn = rawLine.Length - rawLine.TrimStart().Length;

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "import",
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = candidate.Trim(),
            },
            rawLine);
        return true;
    }

    private static bool HasGoSymbol(List<SymbolRecord> symbols, long fileId, int lineNumber, string kind, string name)
    {
        return symbols.Any(symbol =>
            symbol.FileId == fileId
            && symbol.Line == lineNumber
            && symbol.Kind == kind
            && symbol.Name == name);
    }

    private static void AssignGoMethodReceiverContainers(List<SymbolRecord> symbols)
    {
        var typeKinds = symbols
            .Where(symbol => symbol.Kind is "struct" or "interface" or "class")
            .GroupBy(symbol => GetGoReceiverTypeLookupName(symbol.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Kind, StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function"
                || string.IsNullOrWhiteSpace(symbol.Signature)
                || !TryGetGoMethodReceiverTypeName(symbol.Signature, out var receiverTypeName))
            {
                continue;
            }

            var receiverLookupName = GetGoReceiverTypeLookupName(receiverTypeName);
            symbol.ContainerName = receiverLookupName;
            symbol.ContainerKind = typeKinds.TryGetValue(receiverLookupName, out var kind) ? kind : "class";
        }
    }

    private static void ClassifyGoFunctionRoles(List<SymbolRecord> symbols, string? filePath)
    {
        var isTestFile = filePath != null
            && filePath.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase);

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function")
                continue;

            symbol.SubKind = GetGoFunctionSubKind(symbol, isTestFile);
        }
    }

    private static string? GetGoFunctionSubKind(SymbolRecord symbol, bool isTestFile)
    {
        var signature = symbol.Signature ?? string.Empty;
        if (string.Equals(symbol.Name, "init", StringComparison.Ordinal)
            && symbol.ContainerName == null
            && GoSignatureHasNoParameters(signature))
        {
            return "init";
        }

        if (!isTestFile)
            return null;

        if (string.Equals(symbol.Name, "TestMain", StringComparison.Ordinal)
            && GoSignatureHasParameterType(signature, "testing.M"))
        {
            return "test_main";
        }

        if (IsGoExportedPrefixedName(symbol.Name, "Test")
            && GoSignatureHasParameterType(signature, "testing.T"))
        {
            return "test";
        }

        if (IsGoExportedPrefixedName(symbol.Name, "Benchmark")
            && GoSignatureHasParameterType(signature, "testing.B"))
        {
            return "benchmark";
        }

        if (IsGoExportedPrefixedName(symbol.Name, "Fuzz")
            && GoSignatureHasParameterType(signature, "testing.F"))
        {
            return "fuzz";
        }

        if (symbol.Name.StartsWith("Example", StringComparison.Ordinal)
            && GoSignatureHasNoParameters(signature)
            && GoSignatureHasNoReturnValue(signature))
        {
            return "example";
        }

        return isTestFile ? "test_helper" : null;
    }

    private static bool IsGoExportedPrefixedName(string name, string prefix)
        => name.Length > prefix.Length
           && name.StartsWith(prefix, StringComparison.Ordinal)
           && !char.IsLower(name[prefix.Length]);

    private static bool GoSignatureHasParameterType(string signature, string typeName)
    {
        var open = signature.IndexOf('(');
        if (open < 0)
            return false;
        var close = ReferenceExtractor.FindMatchingChar(signature, open, '(', ')');
        if (close <= open)
            return false;

        var parameters = signature[(open + 1)..close].Replace("*", string.Empty, StringComparison.Ordinal);
        var bareTypeName = GetGoReceiverTypeLookupName(typeName);
        return ContainsGoTypeToken(parameters, typeName)
            || ContainsGoTypeToken(parameters, bareTypeName);
    }

    private static bool ContainsGoTypeToken(string text, string typeName)
    {
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var index = text.IndexOf(typeName, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var before = index == 0 ? '\0' : text[index - 1];
            var afterIndex = index + typeName.Length;
            var after = afterIndex >= text.Length ? '\0' : text[afterIndex];
            if (!IsGoTypeTokenPart(before) && !IsGoTypeTokenPart(after))
                return true;

            searchStart = afterIndex;
        }

        return false;
    }

    private static bool IsGoTypeTokenPart(char ch)
        => ch == '_' || ch == '.' || char.IsLetterOrDigit(ch);

    private static bool GoSignatureHasNoParameters(string signature)
    {
        var open = signature.IndexOf('(');
        if (open < 0)
            return false;
        var close = ReferenceExtractor.FindMatchingChar(signature, open, '(', ')');
        return close > open && string.IsNullOrWhiteSpace(signature[(open + 1)..close]);
    }

    private static bool GoSignatureHasNoReturnValue(string signature)
    {
        var open = signature.IndexOf('(');
        if (open < 0)
            return false;
        var close = ReferenceExtractor.FindMatchingChar(signature, open, '(', ')');
        if (close < 0 || close + 1 >= signature.Length)
            return true;

        var trailing = signature[(close + 1)..].TrimStart();
        return trailing.Length == 0 || trailing[0] == '{';
    }

    private static bool TryGetGoMethodReceiverTypeName(string signature, out string receiverTypeName)
    {
        receiverTypeName = string.Empty;
        var funcIndex = signature.IndexOf("func", StringComparison.Ordinal);
        if (funcIndex < 0)
            return false;

        var open = funcIndex + "func".Length;
        while (open < signature.Length && char.IsWhiteSpace(signature[open]))
            open++;
        if (open >= signature.Length || signature[open] != '(')
            return false;

        var close = ReferenceExtractor.FindMatchingChar(signature, open, '(', ')');
        if (close <= open + 1)
            return false;

        var receiver = signature[(open + 1)..close].Trim();
        if (receiver.Length == 0)
            return false;

        var typeText = receiver;
        if (IsGoSymbolIdentifierStart(receiver[0]))
        {
            var cursor = 1;
            while (cursor < receiver.Length && IsGoSymbolIdentifierPart(receiver[cursor]))
                cursor++;

            var afterReceiverName = SkipGoSymbolWhitespace(receiver, cursor);
            if (afterReceiverName > cursor && afterReceiverName < receiver.Length)
                typeText = receiver[afterReceiverName..];
        }

        typeText = typeText.Trim();
        while (typeText.StartsWith("*", StringComparison.Ordinal))
            typeText = typeText[1..].TrimStart();

        var genericStart = typeText.IndexOf('[');
        if (genericStart >= 0)
            typeText = typeText[..genericStart];

        var dot = typeText.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < typeText.Length)
            typeText = typeText[(dot + 1)..];

        receiverTypeName = typeText.Trim();
        return receiverTypeName.Length > 0;
    }

    private static string GetGoReceiverTypeLookupName(string typeName)
    {
        var lookupName = typeName.Trim();
        while (lookupName.StartsWith("*", StringComparison.Ordinal))
            lookupName = lookupName[1..].TrimStart();

        var genericStart = lookupName.IndexOf('[');
        if (genericStart >= 0)
            lookupName = lookupName[..genericStart];

        var dot = lookupName.LastIndexOf('.');
        return dot >= 0 && dot + 1 < lookupName.Length
            ? lookupName[(dot + 1)..].Trim()
            : lookupName;
    }

    private static int SkipGoSymbolWhitespace(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;
        return start;
    }

    private static bool IsGoSymbolIdentifierStart(char ch) =>
        ch == '_' || char.IsLetter(ch);

    private static bool IsGoSymbolIdentifierPart(char ch) =>
        ch == '_' || char.IsLetterOrDigit(ch);

    private static void ExtractGoGroupedDeclarations(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        string? blockKind = null;
        ExtractGoInterfaceMethods(fileId, lines, symbols);
        var typeBodyDepth = 0;
        string? typeBodyKind = null;
        var goBlockDepth = 0;
        var goBlockInBlockComment = false;
        var goBlockInRawString = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (typeBodyDepth > 0)
            {
                if (typeBodyKind == "struct")
                    TryAddGoStructEmbeddedTypeSymbols(fileId, line, i, symbols, trimmed);

                typeBodyDepth += CountGoBraceDelta(line);
                if (typeBodyDepth < 0)
                    typeBodyDepth = 0;
                if (typeBodyDepth == 0)
                    typeBodyKind = null;
                continue;
            }

            if (goBlockDepth > 0)
            {
                goBlockDepth += CountGoCodeBraceDelta(line, ref goBlockInBlockComment, ref goBlockInRawString);
                if (goBlockDepth < 0)
                    goBlockDepth = 0;
                continue;
            }

            if (trimmed.Length == 0
                || trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                TryAddGoDirectiveSymbol(fileId, line, i, symbols, trimmed);
                continue;
            }

            if (trimmed.StartsWith(")", StringComparison.Ordinal))
            {
                blockKind = null;
                typeBodyDepth = 0;
                typeBodyKind = null;
                continue;
            }

            if (blockKind is not null)
            {
                switch (blockKind)
                {
                    case "type":
                        TryAddGoTypeSymbol(fileId, line, i, symbols, trimmed, ref typeBodyDepth, ref typeBodyKind);
                        break;
                    case "const":
                    case "var":
                        TryAddGoValueSymbol(fileId, line, i, symbols, trimmed);
                        break;
                }

                continue;
            }

            if (trimmed.StartsWith("type", StringComparison.Ordinal)
                && trimmed["type".Length..].TrimStart().StartsWith("(", StringComparison.Ordinal))
            {
                blockKind = "type";
                continue;
            }

            if (trimmed.StartsWith("const", StringComparison.Ordinal)
                && trimmed["const".Length..].TrimStart().StartsWith("(", StringComparison.Ordinal))
            {
                blockKind = "const";
                continue;
            }

            if (trimmed.StartsWith("const", StringComparison.Ordinal))
            {
                TryAddGoValueSymbol(fileId, line, i, symbols, trimmed["const".Length..].TrimStart());
                continue;
            }

            if (trimmed.StartsWith("var", StringComparison.Ordinal)
                && trimmed["var".Length..].TrimStart().StartsWith("(", StringComparison.Ordinal))
            {
                blockKind = "var";
                continue;
            }

            if (trimmed.StartsWith("var", StringComparison.Ordinal))
            {
                TryAddGoValueSymbol(fileId, line, i, symbols, trimmed["var".Length..].TrimStart());
                continue;
            }

            if (trimmed.StartsWith("type", StringComparison.Ordinal))
            {
                TryAddGoTypeSymbol(fileId, line, i, symbols, trimmed, ref typeBodyDepth, ref typeBodyKind);
                continue;
            }

            goBlockDepth += CountGoCodeBraceDelta(line, ref goBlockInBlockComment, ref goBlockInRawString);
            if (goBlockDepth < 0)
                goBlockDepth = 0;
        }
    }

}
