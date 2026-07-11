using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptExportedObjectLiteralProperties(
        long fileId,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        List<JavaScriptClassScanTarget> objectLiteralTargets)
    {
        foreach (var target in objectLiteralTargets)
        {
            if (!target.IsExported)
                continue;

            var braceDepth = 0;
            var parenDepth = 0;
            var bracketDepth = 0;
            var skippingPropertyValue = false;
            var existingContainerSymbolNames = BuildJavaScriptTypeScriptObjectContainerSymbolNameSet(symbols, target.ContainerName);

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

                        if (scanColumn + 2 < sanitizedLine.Length
                            && sanitizedLine[scanColumn] == '.'
                            && sanitizedLine[scanColumn + 1] == '.'
                            && sanitizedLine[scanColumn + 2] == '.')
                        {
                            scanColumn += 3;
                            skippingPropertyValue = true;
                            continue;
                        }

                        if (TryReadJavaScriptTypeScriptIdentifierObjectLiteralKeyName(
                                sanitizedLine,
                                scanColumn,
                                out var propertyName,
                                out var identifierValueStartColumn))
                        {
                            AddJavaScriptTypeScriptExportedObjectLiteralPropertySymbol(
                                fileId,
                                rawLines,
                                sanitizedLines,
                                symbols,
                                existingContainerSymbolNames,
                                target.ContainerName,
                                propertyName,
                                lineIndex,
                                scanColumn,
                                identifierValueStartColumn);

                            scanColumn = identifierValueStartColumn;
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
                            AddJavaScriptTypeScriptExportedObjectLiteralPropertySymbol(
                                fileId,
                                rawLines,
                                sanitizedLines,
                                symbols,
                                existingContainerSymbolNames,
                                target.ContainerName,
                                literalPropertyName,
                                lineIndex,
                                scanColumn,
                                literalValueStartColumn);

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
                            AddJavaScriptTypeScriptExportedObjectLiteralPropertySymbol(
                                fileId,
                                rawLines,
                                sanitizedLines,
                                symbols,
                                existingContainerSymbolNames,
                                target.ContainerName,
                                computedLiteralPropertyName,
                                lineIndex,
                                scanColumn,
                                computedLiteralValueStartColumn);

                            scanColumn = computedLiteralValueStartColumn;
                            skippingPropertyValue = true;
                            continue;
                        }

                        if (TrySkipJavaScriptTypeScriptNonIdentifierObjectLiteralKey(sanitizedLine, ref scanColumn))
                        {
                            skippingPropertyValue = true;
                            continue;
                        }

                        if (TryReadJavaScriptTypeScriptShorthandObjectLiteralKeyName(
                                sanitizedLine,
                                scanColumn,
                                out var shorthandPropertyName,
                                out var shorthandEndColumn))
                        {
                            AddJavaScriptTypeScriptExportedObjectLiteralPropertySymbol(
                                fileId,
                                rawLines,
                                sanitizedLines,
                                symbols,
                                existingContainerSymbolNames,
                                target.ContainerName,
                                shorthandPropertyName,
                                lineIndex,
                                scanColumn,
                                shorthandEndColumn);

                            scanColumn = shorthandEndColumn;
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

    private static HashSet<string> BuildJavaScriptTypeScriptObjectContainerSymbolNameSet(
        IReadOnlyList<SymbolRecord> symbols,
        string? containerName)
    {
        var existingContainerSymbolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.ContainerKind == "object" && symbol.ContainerName == containerName)
                existingContainerSymbolNames.Add(symbol.Name);
        }

        return existingContainerSymbolNames;
    }

    private static void AddJavaScriptTypeScriptExportedObjectLiteralPropertySymbol(
        long fileId,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        HashSet<string> existingContainerSymbolNames,
        string containerName,
        string propertyName,
        int lineIndex,
        int startColumn,
        int valueStartColumn)
    {
        if (propertyName.Length == 0 || !existingContainerSymbolNames.Add(propertyName))
            return;

        var signature = BuildJavaScriptTypeScriptObjectLiteralPropertySignature(
            rawLines[lineIndex],
            sanitizedLines[lineIndex],
            startColumn,
            valueStartColumn);

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
                ContainerKind = "object",
                ContainerName = containerName,
                Visibility = "export",
            },
            rawLines[lineIndex]);
    }

    private const int JavaScriptTypeScriptObjectLiteralPropertySignatureMaxLength = 240;

    private static string BuildJavaScriptTypeScriptObjectLiteralPropertySignature(
        string rawLine,
        string sanitizedLine,
        int startColumn,
        int valueStartColumn)
    {
        var lineLength = Math.Min(rawLine.Length, sanitizedLine.Length);
        var signatureStart = Math.Clamp(startColumn, 0, lineLength);
        var scanColumn = Math.Clamp(valueStartColumn, signatureStart, sanitizedLine.Length);
        var endColumn = sanitizedLine.Length;
        var braceDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;

        while (scanColumn < sanitizedLine.Length)
        {
            var ch = sanitizedLine[scanColumn];
            if (braceDepth == 0 && parenDepth == 0 && bracketDepth == 0 && ch is ',' or '}' or ';')
            {
                endColumn = scanColumn;
                break;
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

        endColumn = Math.Clamp(endColumn, signatureStart, rawLine.Length);
        while (endColumn > signatureStart && char.IsWhiteSpace(rawLine[endColumn - 1]))
            endColumn--;

        var signature = rawLine[signatureStart..endColumn].Trim();
        if (signature.Length == 0)
            signature = rawLine.Trim();

        return signature.Length <= JavaScriptTypeScriptObjectLiteralPropertySignatureMaxLength
            ? signature
            : signature[..(JavaScriptTypeScriptObjectLiteralPropertySignatureMaxLength - 3)] + "...";
    }

    private static bool TryReadJavaScriptTypeScriptIdentifierObjectLiteralKeyName(
        string sanitizedLine,
        int startColumn,
        out string propertyName,
        out int valueStartColumn)
    {
        propertyName = string.Empty;
        valueStartColumn = startColumn;

        var probe = startColumn;
        if (!TryReadJavaScriptTypeScriptIdentifierToken(sanitizedLine, ref probe, out propertyName))
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

    private static bool TryReadJavaScriptTypeScriptShorthandObjectLiteralKeyName(
        string sanitizedLine,
        int startColumn,
        out string propertyName,
        out int endColumn)
    {
        propertyName = string.Empty;
        endColumn = startColumn;

        var probe = startColumn;
        if (!TryReadJavaScriptTypeScriptIdentifierToken(sanitizedLine, ref probe, out propertyName))
            return false;

        while (probe < sanitizedLine.Length && char.IsWhiteSpace(sanitizedLine[probe]))
            probe++;

        if (probe < sanitizedLine.Length && sanitizedLine[probe] is not ',' and not '}')
        {
            propertyName = string.Empty;
            return false;
        }

        endColumn = probe;
        return true;
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
            var rawKey = rawLine.AsSpan(keyStartColumn, rawEndColumn - keyStartColumn).Trim();
            if (rawKey.Length < 2
                || rawKey[0] != rawKey[^1]
                || rawKey[0] is not ('\'' or '"'))
            {
                return false;
            }

            propertyName = rawKey[1..^1].ToString();
        }
        else if (char.IsDigit(sanitizedLine[startColumn]))
        {
            if (!TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
                return false;

            var rawEndColumn = Math.Min(probe, rawLine.Length);
            propertyName = rawLine.AsSpan(keyStartColumn, rawEndColumn - keyStartColumn).Trim().ToString();
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
            var rawKey = rawLine.AsSpan(keyStartColumn, rawEndColumn - keyStartColumn).Trim();
            if (rawKey.Length < 2
                || rawKey[0] != rawKey[^1]
                || rawKey[0] is not ('\'' or '"'))
            {
                return false;
            }

            propertyName = rawKey[1..^1].ToString();
        }
        else if (char.IsDigit(sanitizedLine[probe]))
        {
            var keyStartColumn = probe;
            if (!TryReadJavaScriptTypeScriptNumericLiteralToken(sanitizedLine, ref probe, out _))
                return false;

            var rawEndColumn = Math.Min(probe, rawLine.Length);
            propertyName = rawLine.AsSpan(keyStartColumn, rawEndColumn - keyStartColumn).Trim().ToString();
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
}
