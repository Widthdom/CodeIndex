using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeIndex.Diagnostics;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    internal const int StructuredDataMaxJsonDepth = 64;
    internal const int StructuredDataMaxYamlDepth = 64;
    internal const int StructuredDataMaxSymbols = 4096;
    internal const int StructuredDataMaxTraversalNodes = 8192;
    internal const int StructuredDataMaxPathLength = 1024;
    internal const int StructuredDataMaxSignatureLength = 512;
    internal const int StructuredDataMaxJsonParseChars = 1_000_000;
    internal const int StructuredDataMaxJsonParseUtf8Bytes = StructuredDataMaxJsonParseChars * 4;

    private static readonly JsonReaderOptions StructuredJsonReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = StructuredDataMaxJsonDepth + 2,
    };

    private static readonly Regex JsonFallbackPropertyRegex = new(
        @"^\s*""(?<name>(?:\\.|[^""\\])+)""\s*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex YamlMappingKeyRegex = new(
        @"^(?<indent>[ ]*)(?:-\s*)?(?:""(?<double>(?:[^""]|"""")+)""|'(?<single>(?:[^']|'')+)'|(?<plain>[A-Za-z0-9_.-][A-Za-z0-9_. -]*))\s*:\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly record struct YamlPathFrame(int Indent, string Path, string? SymbolPath);

    private static List<SymbolRecord> ExtractJsonSymbols(long fileId, string content, string[] lines)
    {
        // JsonDocument.Parse builds a full DOM, so large JSON files use the capped line fallback.
        if (content.Length > StructuredDataMaxJsonParseChars)
            return ExtractJsonFallbackSymbols(fileId, lines);

        try
        {
            using var document = BoundedJson.ParseDocument(
                content,
                StructuredDataMaxJsonParseUtf8Bytes,
                StructuredDataMaxJsonDepth + 2,
                JsonCommentHandling.Skip,
                allowTrailingCommas: true);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            var rootProperties = document.RootElement.EnumerateObject();
            if (!rootProperties.MoveNext())
                return [];

            var symbols = CreateSymbolListForLines(lines.Length);
            int[]? lineStarts = null;
            var searchOffset = 0;
            var traversalNodes = 0;
            var truncated = false;
            var propertyLines = content.IndexOf('\n', StringComparison.Ordinal) < 0
                && content.IndexOf('\r', StringComparison.Ordinal) < 0
                ? null
                : BuildJsonPropertyLineQueues(content);
            ExtractJsonObjectSymbols(
                fileId,
                content,
                lines,
                ref lineStarts,
                document.RootElement,
                parentPath: null,
                ref searchOffset,
                symbols,
                propertyLines,
                depth: 0,
                ref traversalNodes,
                ref truncated);
            return symbols;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return ExtractJsonFallbackSymbols(fileId, lines);
        }
    }

    private static void ExtractJsonObjectSymbols(
        long fileId,
        string content,
        string[] lines,
        ref int[]? lineStarts,
        JsonElement element,
        string? parentPath,
        ref int searchOffset,
        List<SymbolRecord> symbols,
        Dictionary<string, Queue<int>>? propertyLines,
        int depth,
        ref int traversalNodes,
        ref bool truncated)
    {
        if (depth >= StructuredDataMaxJsonDepth)
        {
            AddStructuredDataDiagnosticSymbol(symbols, fileId, "structured_data_depth_budget_exceeded", line: 1, lines, "Structured data traversal exceeded the maximum depth; nested symbols were truncated.", ref truncated);
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (traversalNodes >= StructuredDataMaxTraversalNodes)
            {
                AddStructuredDataDiagnosticSymbol(symbols, fileId, "structured_data_traversal_budget_exceeded", line: 1, lines, "Structured data traversal exceeded the per-file node budget; remaining nodes were truncated.", ref truncated);
                return;
            }

            traversalNodes++;
            var propertyName = property.Name;
            var nameLength = string.IsNullOrEmpty(parentPath)
                ? propertyName.Length
                : parentPath.Length + 1 + propertyName.Length;
            if (nameLength > StructuredDataMaxPathLength)
            {
                if (propertyLines != null)
                    _ = TryDequeueJsonPropertyLine(propertyLines, propertyName, out _);
                DrainJsonPropertyLines(property.Value, propertyLines);
                continue;
            }

            var name = string.IsNullOrEmpty(parentPath)
                ? propertyName
                : parentPath + "." + propertyName;
            var propertyOffset = FindJsonPropertyOffset(content, propertyName, ref searchOffset);
            var line = propertyLines != null
                && TryDequeueJsonPropertyLine(propertyLines, propertyName, out var mappedLine)
                    ? mappedLine
                    : FindLineNumberForOffset(lineStarts ??= BuildLineStarts(lines), propertyOffset);

            var kind = property.Value.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                _ => "property",
            };

            if (!TryAddStructuredDataSymbol(fileId, kind, name, line, lines, parentPath, symbols, "structured_data_symbol_budget_exceeded", ref truncated))
                return;

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                ExtractJsonObjectSymbols(fileId, content, lines, ref lineStarts, property.Value, name, ref searchOffset, symbols, propertyLines, depth + 1, ref traversalNodes, ref truncated);
                if (truncated)
                    return;
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                ExtractJsonArraySymbols(
                    fileId,
                    content,
                    lines,
                    ref lineStarts,
                    property.Value,
                    name,
                    line,
                    ref searchOffset,
                    symbols,
                    propertyLines,
                    depth + 1,
                    ref traversalNodes,
                    ref truncated);
                if (truncated)
                    return;
            }
        }
    }

    private static void ExtractJsonArraySymbols(
        long fileId,
        string content,
        string[] lines,
        ref int[]? lineStarts,
        JsonElement element,
        string arrayPath,
        int arrayLine,
        ref int searchOffset,
        List<SymbolRecord> symbols,
        Dictionary<string, Queue<int>>? propertyLines,
        int depth,
        ref int traversalNodes,
        ref bool truncated)
    {
        if (depth >= StructuredDataMaxJsonDepth)
        {
            AddStructuredDataDiagnosticSymbol(symbols, fileId, "structured_data_depth_budget_exceeded", line: 1, lines, "Structured data traversal exceeded the maximum depth; nested symbols were truncated.", ref truncated);
            return;
        }

        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (traversalNodes >= StructuredDataMaxTraversalNodes)
            {
                AddStructuredDataDiagnosticSymbol(symbols, fileId, "structured_data_traversal_budget_exceeded", line: 1, lines, "Structured data traversal exceeded the per-file node budget; remaining nodes were truncated.", ref truncated);
                return;
            }

            traversalNodes++;
            var itemPath = $"{arrayPath}[{index}]";
            if (itemPath.Length > StructuredDataMaxPathLength)
            {
                DrainJsonPropertyLines(item, propertyLines);
                index++;
                continue;
            }

            var kind = item.ValueKind switch
            {
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                _ => "value",
            };
            var itemLine = FindJsonElementLine(content, lines, ref lineStarts, item, arrayLine, ref searchOffset);
            if (!TryAddStructuredDataSymbol(fileId, kind, itemPath, itemLine, lines, arrayPath, symbols, "structured_data_symbol_budget_exceeded", ref truncated))
                return;

            if (item.ValueKind == JsonValueKind.Object)
            {
                ExtractJsonObjectSymbols(fileId, content, lines, ref lineStarts, item, itemPath, ref searchOffset, symbols, propertyLines, depth + 1, ref traversalNodes, ref truncated);
            }
            else if (item.ValueKind == JsonValueKind.Array)
            {
                ExtractJsonArraySymbols(fileId, content, lines, ref lineStarts, item, itemPath, itemLine, ref searchOffset, symbols, propertyLines, depth + 1, ref traversalNodes, ref truncated);
            }

            if (truncated)
                return;
            index++;
        }
    }

    private static int FindJsonElementLine(
        string content,
        string[] lines,
        ref int[]? lineStarts,
        JsonElement element,
        int fallbackLine,
        ref int searchOffset)
    {
        if (lines.Length <= 1)
            return fallbackLine;

        var rawText = element.GetRawText();
        var offset = content.IndexOf(rawText, searchOffset, StringComparison.Ordinal);
        if (offset < 0 && searchOffset > 0)
            offset = content.IndexOf(rawText, StringComparison.Ordinal);
        if (offset < 0)
            return fallbackLine;

        searchOffset = offset + 1;
        return FindLineNumberForOffset(lineStarts ??= BuildLineStarts(lines), offset);
    }

    private static void DrainJsonPropertyLines(JsonElement element, Dictionary<string, Queue<int>>? propertyLines)
    {
        if (propertyLines == null)
            return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                _ = TryDequeueJsonPropertyLine(propertyLines, property.Name, out _);
                DrainJsonPropertyLines(property.Value, propertyLines);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                DrainJsonPropertyLines(item, propertyLines);
        }
    }

    private static List<SymbolRecord> ExtractJsonFallbackSymbols(long fileId, string[] lines)
    {
        List<SymbolRecord>? symbols = null;
        var truncated = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!MayStartJsonPropertyLine(lines[i]) || lines[i].IndexOf(':') < 0)
                continue;

            var match = JsonFallbackPropertyRegex.Match(lines[i]);
            if (!match.Success)
                continue;

            var name = UnescapeJsonPropertyName(match.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (name.Length > StructuredDataMaxPathLength)
                continue;

            if (!TryAddStructuredDataSymbol(
                fileId,
                "property",
                name,
                i + 1,
                lines,
                parentPath: null,
                symbols ??= CreateSymbolListForLines(lines.Length),
                "structured_data_symbol_budget_exceeded",
                ref truncated))
                break;
        }

        return symbols ?? [];
    }

    private static List<SymbolRecord> ExtractJsonLinesSymbols(long fileId, string content, string[] lines)
    {
        var symbols = CreateSymbolListForLines(lines.Length);
        var recordIndex = 0;
        var truncated = false;
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var recordText = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(recordText))
                continue;

            var recordPath = $"[{recordIndex}]";
            try
            {
                using var document = BoundedJson.ParseDocument(
                    recordText,
                    StructuredDataMaxJsonParseUtf8Bytes,
                    StructuredDataMaxJsonDepth + 2,
                    JsonCommentHandling.Skip,
                    allowTrailingCommas: true);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    recordIndex++;
                    continue;
                }

                if (symbols.Count >= StructuredDataMaxSymbols)
                    return symbols;
                if (!TryAddStructuredDataSymbol(fileId, "record", recordPath, lineIndex + 1, lines, null, symbols, "structured_data_symbol_budget_exceeded", ref truncated))
                    break;

                var recordSymbols = ExtractJsonSymbols(fileId, recordText, [recordText]);
                foreach (var symbol in recordSymbols)
                {
                    if (symbol.Kind == "extraction_diagnostic")
                        continue;
                    if (symbols.Count >= StructuredDataMaxSymbols)
                        return symbols;

                    symbol.Name = recordPath + "." + symbol.Name;
                    symbol.Line = lineIndex + 1;
                    symbol.StartLine = lineIndex + 1;
                    symbol.EndLine = lineIndex + 1;
                    symbol.Signature = LimitStructuredDataSignature(recordText.Trim());
                    symbol.ContainerName = symbol.ContainerName == null
                        ? recordPath
                        : recordPath + "." + symbol.ContainerName;
                    symbol.ContainerQualifiedName = symbol.ContainerName;
                    symbols.Add(symbol);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // A malformed JSONL record must not flatten keys from other records.
            }

            recordIndex++;
        }

        return symbols;
    }

    private static List<SymbolRecord> ExtractYamlSymbols(long fileId, string[] lines)
    {
        List<SymbolRecord>? symbols = null;
        List<YamlPathFrame>? stack = null;
        Dictionary<string, int>? sequenceIndexes = null;
        int? blockScalarIndent = null;
        var traversalNodes = 0;
        var truncated = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (traversalNodes >= StructuredDataMaxTraversalNodes)
            {
                AddStructuredDataDiagnosticSymbol(symbols ??= [], fileId, "structured_data_traversal_budget_exceeded", i + 1, lines, "Structured data traversal exceeded the per-file node budget; remaining nodes were truncated.", ref truncated);
                break;
            }

            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var indent = CountLeadingSpaces(line);
            if (blockScalarIndent.HasValue)
            {
                if (indent > blockScalarIndent.Value)
                    continue;
                blockScalarIndent = null;
            }

            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.IsEmpty
                || trimmed[0] == '#'
                || trimmed.SequenceEqual("---")
                || trimmed.SequenceEqual("..."))
            {
                continue;
            }

            var isSequenceItem = trimmed[0] == '-'
                && (trimmed.Length == 1 || char.IsWhiteSpace(trimmed[1]));
            var mappingIndent = indent;
            if (isSequenceItem)
            {
                while (stack != null && stack.Count > 0 && indent <= stack[^1].Indent)
                    stack.RemoveAt(stack.Count - 1);
                var sequenceParent = stack == null || stack.Count == 0 ? null : stack[^1].Path;
                var sequenceSymbolParent = stack == null || stack.Count == 0 ? null : stack[^1].SymbolPath;
                var sequenceKey = $"{indent}:{sequenceParent}";
                sequenceIndexes ??= new Dictionary<string, int>(StringComparer.Ordinal);
                sequenceIndexes.TryGetValue(sequenceKey, out var sequenceIndex);
                sequenceIndexes[sequenceKey] = sequenceIndex + 1;
                (stack ??= []).Add(new YamlPathFrame(
                    indent,
                    $"{sequenceParent}[{sequenceIndex}]",
                    sequenceSymbolParent));

                var rawSequenceValue = trimmed[1..];
                var sequenceValueStart = rawSequenceValue.TrimStart();
                mappingIndent = indent + 1 + rawSequenceValue.Length - sequenceValueStart.Length;
                var sequenceValue = StripYamlInlineComment(sequenceValueStart).Trim();
                if (!sequenceValue.IsEmpty && (sequenceValue[0] == '|' || sequenceValue[0] == '>'))
                    blockScalarIndent = indent;
                if (sequenceValue.IsEmpty
                    || sequenceValue[0] == '#'
                    || line.IndexOf(':') < 0)
                {
                    continue;
                }
            }

            if (line.IndexOf(':') < 0)
                continue;

            var match = YamlMappingKeyRegex.Match(line);
            if (!match.Success)
                continue;

            var key = ExtractYamlKey(match);
            if (key.Length == 0)
                continue;

            if (!isSequenceItem)
            {
                while (stack != null && stack.Count > 0 && indent <= stack[^1].Indent)
                    stack.RemoveAt(stack.Count - 1);
            }

            var parentPath = stack == null || stack.Count == 0 ? null : stack[^1].Path;
            var symbolParentPath = stack == null || stack.Count == 0 ? null : stack[^1].SymbolPath;
            var pathLength = string.IsNullOrEmpty(parentPath)
                ? key.Length
                : parentPath.Length + 1 + key.Length;
            if ((stack?.Count ?? 0) >= StructuredDataMaxYamlDepth || pathLength > StructuredDataMaxPathLength)
                continue;
            var path = string.IsNullOrEmpty(parentPath) ? key : parentPath + "." + key;

            var value = StripYamlInlineComment(match.Groups["value"].ValueSpan).Trim();
            var isBlockScalar = !value.IsEmpty && (value[0] == '|' || value[0] == '>');
            var isContainer = !isBlockScalar && IsYamlContainerValue(value);
            var kind = isContainer ? "namespace" : "property";

            traversalNodes++;
            if (!TryAddStructuredDataSymbol(
                fileId,
                kind,
                path,
                i + 1,
                lines,
                symbolParentPath,
                symbols ??= CreateSymbolListForLines(lines.Length),
                "structured_data_symbol_budget_exceeded",
                ref truncated,
                parentPath))
                break;

            if (isBlockScalar)
            {
                blockScalarIndent = indent;
            }
            else if (isContainer)
            {
                (stack ??= []).Add(new YamlPathFrame(mappingIndent, path, path));
            }
        }

        return symbols ?? [];
    }

    private static bool TryAddStructuredDataSymbol(
        long fileId,
        string kind,
        string name,
        int line,
        string[] lines,
        string? parentPath,
        List<SymbolRecord> symbols,
        string category,
        ref bool truncated,
        string? qualifiedParentPath = null)
    {
        if (symbols.Count < StructuredDataMaxSymbols)
        {
            symbols.Add(CreateStructuredDataSymbol(
                fileId,
                kind,
                name,
                line,
                lines,
                parentPath,
                qualifiedParentPath));
            return true;
        }

        AddStructuredDataDiagnosticSymbol(symbols, fileId, category, line, lines, "Structured data symbol extraction exceeded the per-file symbol budget; remaining symbols were truncated.", ref truncated);
        return false;
    }

    private static bool TryAddBoundedStructuredDataSymbol(
        List<SymbolRecord> symbols,
        SymbolRecord symbol)
    {
        // Retain one overflow marker so TrimStructuredDataSymbols can preserve the existing
        // diagnostic behavior, but never let dense supplemental scans grow without bound.
        symbols.Add(symbol);
        return symbols.Count <= StructuredDataMaxSymbols;
    }

    private static void AddStructuredDataDiagnosticSymbol(
        List<SymbolRecord> symbols,
        long fileId,
        string category,
        int line,
        string[] lines,
        string message,
        ref bool added)
    {
        if (added)
            return;

        added = true;
        var signatureIndex = Math.Clamp(line - 1, 0, Math.Max(0, lines.Length - 1));
        var signature = lines.Length == 0 ? message : $"{message} {lines[signatureIndex].Trim()}";
        var diagnostic = new SymbolRecord
        {
            FileId = fileId,
            Kind = "extraction_diagnostic",
            Name = category,
            Line = Math.Max(1, line),
            StartLine = Math.Max(1, line),
            EndLine = Math.Max(1, line),
            Signature = LimitStructuredDataSignature(signature),
        };

        if (symbols.Count >= StructuredDataMaxSymbols)
            symbols[^1] = diagnostic;
        else
            symbols.Add(diagnostic);
    }

    private static List<SymbolRecord> TrimStructuredDataSymbols(
        List<SymbolRecord> symbols,
        long fileId,
        string category,
        string[] lines)
    {
        if (symbols.Count <= StructuredDataMaxSymbols)
            return symbols;

        var retained = new List<SymbolRecord>(StructuredDataMaxSymbols);
        for (var index = 0; index < StructuredDataMaxSymbols; index++)
            retained.Add(symbols[index]);

        var added = false;
        AddStructuredDataDiagnosticSymbol(
            retained,
            fileId,
            category,
            line: 1,
            lines,
            "Structured data symbol extraction exceeded the per-file symbol budget; remaining symbols were truncated.",
            ref added);
        return retained;
    }

    private static SymbolRecord CreateStructuredDataSymbol(
        long fileId,
        string kind,
        string name,
        int line,
        string[] lines,
        string? parentPath,
        string? qualifiedParentPath)
    {
        var signatureIndex = Math.Clamp(line - 1, 0, Math.Max(0, lines.Length - 1));
        return new SymbolRecord
        {
            FileId = fileId,
            Kind = kind,
            Name = name,
            Line = line,
            StartLine = line,
            EndLine = line,
            Signature = lines.Length == 0 ? null : LimitStructuredDataLineSignature(lines[signatureIndex]),
            ContainerKind = parentPath == null ? null : "namespace",
            ContainerName = parentPath,
            ContainerQualifiedName = qualifiedParentPath ?? parentPath,
        };
    }

    private static string LimitStructuredDataSignature(string signature) =>
        signature.Length <= StructuredDataMaxSignatureLength
            ? signature
            : signature[..StructuredDataMaxSignatureLength];

    private static string LimitStructuredDataLineSignature(string line)
    {
        var trimmed = line.AsSpan().Trim();
        if (trimmed.Length > StructuredDataMaxSignatureLength)
            trimmed = trimmed[..StructuredDataMaxSignatureLength];
        return trimmed.ToString();
    }

    private static Dictionary<string, Queue<int>> BuildJsonPropertyLineQueues(string content)
    {
        var propertyLines = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(content);
        var byteLineStarts = Utf8LineStarts.Build(bytes);
        var reader = new Utf8JsonReader(bytes, StructuredJsonReaderOptions);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = reader.GetString();
            if (string.IsNullOrEmpty(name))
                continue;

            var byteOffset = (int)Math.Min(reader.TokenStartIndex, (long)bytes.Length);
            if (!propertyLines.TryGetValue(name, out var lines))
            {
                lines = new Queue<int>();
                propertyLines.Add(name, lines);
            }

            lines.Enqueue(FindLineNumberForOffset(byteLineStarts, byteOffset));
        }

        return propertyLines;
    }

    private static bool TryDequeueJsonPropertyLine(
        Dictionary<string, Queue<int>> propertyLines,
        string propertyName,
        out int line)
    {
        if (propertyLines.TryGetValue(propertyName, out var lines) && lines.Count > 0)
        {
            line = lines.Dequeue();
            return true;
        }

        line = 0;
        return false;
    }

    private static int FindJsonPropertyOffset(string content, string propertyName, ref int searchOffset)
    {
        var encodedName = EncodeJsonPropertySearchName(propertyName);
        var offset = content.IndexOf(encodedName, searchOffset, StringComparison.Ordinal);
        if (offset < 0 && searchOffset > 0)
            offset = content.IndexOf(encodedName, StringComparison.Ordinal);

        if (offset >= 0)
        {
            searchOffset = offset + encodedName.Length;
            return offset;
        }

        return Math.Clamp(searchOffset, 0, Math.Max(0, content.Length - 1));
    }

    private static string EncodeJsonPropertySearchName(string propertyName)
        => IsSimpleJsonPropertySearchName(propertyName)
            ? "\"" + propertyName + "\""
            : "\"" + JsonEncodedText.Encode(propertyName).ToString() + "\"";

    private static bool IsSimpleJsonPropertySearchName(string propertyName)
    {
        for (var index = 0; index < propertyName.Length; index++)
        {
            var ch = propertyName[index];
            if ((ch >= 'A' && ch <= 'Z')
                || (ch >= 'a' && ch <= 'z')
                || (ch >= '0' && ch <= '9')
                || ch is '_' or '-' or '.' or ' ')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static int[] BuildLineStarts(string[] lines)
    {
        if (lines.Length <= 1)
            return [0];

        var starts = new int[lines.Length == 0 ? 1 : lines.Length];
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            starts[i] = offset;
            offset += lines[i].Length + 1;
        }

        return starts;
    }

    private static int FindLineNumberForOffset(int[] lineStarts, int offset)
    {
        var index = Array.BinarySearch(lineStarts, offset);
        if (index < 0)
            index = Math.Max(0, ~index - 1);
        return index + 1;
    }

    private static bool MayStartJsonPropertyLine(string line)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (char.IsWhiteSpace(line[index]))
                continue;

            return line[index] == '"';
        }

        return false;
    }

    private static int CountLeadingSpaces(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
            count++;
        return count;
    }

    private static string ExtractYamlKey(Match match)
    {
        if (match.Groups["double"].Success)
            return UnescapeYamlQuotedKey(match.Groups["double"].ValueSpan, "\"\"", "\"");
        if (match.Groups["single"].Success)
            return UnescapeYamlQuotedKey(match.Groups["single"].ValueSpan, "''", "'");
        return match.Groups["plain"].ValueSpan.Trim().ToString();
    }

    private static string UnescapeYamlQuotedKey(ReadOnlySpan<char> value, string escaped, string replacement)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains(escaped, StringComparison.Ordinal))
            return trimmed.ToString();

        return trimmed.ToString().Replace(escaped, replacement, StringComparison.Ordinal);
    }

    private static bool IsYamlContainerValue(ReadOnlySpan<char> value)
        => value.IsEmpty
           || IsYamlAnchorDeclaration(value)
           || value.SequenceEqual("|")
           || value.SequenceEqual(">")
           || value.SequenceEqual("|-")
           || value.SequenceEqual(">-")
           || value.SequenceEqual("|+")
           || value.SequenceEqual(">+");

    private static bool IsYamlAnchorDeclaration(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] != '&')
            return false;

        for (var i = 1; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
                return false;
        }

        return true;
    }

    private static ReadOnlySpan<char> StripYamlInlineComment(ReadOnlySpan<char> value)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (ch == '"' && !inSingle)
                inDouble = !inDouble;
            else if (ch == '#' && !inSingle && !inDouble && (i == 0 || char.IsWhiteSpace(value[i - 1])))
                return value[..i];
        }

        return value;
    }

    private static string UnescapeJsonPropertyName(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        try
        {
            using var document = BoundedJson.ParseDocument(
                "\"" + value + "\"",
                StructuredDataMaxJsonParseUtf8Bytes,
                maxDepth: 1);
            return document.RootElement.GetString() ?? value;
        }
        catch (Exception ex) when (ex is JsonException or System.IO.InvalidDataException or InvalidOperationException)
        {
            return value;
        }
    }
}
