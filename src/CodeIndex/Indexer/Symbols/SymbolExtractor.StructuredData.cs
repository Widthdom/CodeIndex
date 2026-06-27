using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static readonly JsonDocumentOptions StructuredJsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = StructuredDataMaxJsonDepth + 2,
    };

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

    private readonly record struct YamlPathFrame(int Indent, string Path);

    private static List<SymbolRecord> ExtractJsonSymbols(long fileId, string content, string[] lines)
    {
        // JsonDocument.Parse builds a full DOM, so large JSON files use the capped line fallback.
        if (content.Length > StructuredDataMaxJsonParseChars)
            return ExtractJsonFallbackSymbols(fileId, lines);

        var symbols = new List<SymbolRecord>();
        var lineStarts = BuildLineStarts(lines);
        var searchOffset = 0;
        var traversalNodes = 0;
        var truncated = false;

        try
        {
            using var document = JsonDocument.Parse(content, StructuredJsonDocumentOptions);
            var propertyLines = BuildJsonPropertyLineQueues(content);

            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                ExtractJsonObjectSymbols(
                    fileId,
                    content,
                    lines,
                    lineStarts,
                    document.RootElement,
                    parentPath: null,
                    ref searchOffset,
                    symbols,
                    propertyLines,
                    depth: 0,
                    ref traversalNodes,
                    ref truncated);
            }

            return symbols;
        }
        catch (JsonException)
        {
            return ExtractJsonFallbackSymbols(fileId, lines);
        }
    }

    private static void ExtractJsonObjectSymbols(
        long fileId,
        string content,
        string[] lines,
        int[] lineStarts,
        JsonElement element,
        string? parentPath,
        ref int searchOffset,
        List<SymbolRecord> symbols,
        Dictionary<string, Queue<int>> propertyLines,
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
            var name = string.IsNullOrEmpty(parentPath)
                ? property.Name
                : parentPath + "." + property.Name;
            var line = TryDequeueJsonPropertyLine(propertyLines, property.Name, out var mappedLine)
                ? mappedLine
                : FindLineNumberForOffset(lineStarts, FindJsonPropertyOffset(content, property.Name, ref searchOffset));

            if (name.Length > StructuredDataMaxPathLength)
            {
                DrainJsonPropertyLines(property.Value, propertyLines);
                continue;
            }

            var kind = property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                ? "namespace"
                : "property";

            if (!TryAddStructuredDataSymbol(fileId, kind, name, line, lines, parentPath, symbols, "structured_data_symbol_budget_exceeded", ref truncated))
                return;

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                ExtractJsonObjectSymbols(fileId, content, lines, lineStarts, property.Value, name, ref searchOffset, symbols, propertyLines, depth + 1, ref traversalNodes, ref truncated);
                if (truncated)
                    return;
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                        ExtractJsonObjectSymbols(fileId, content, lines, lineStarts, item, name, ref searchOffset, symbols, propertyLines, depth + 1, ref traversalNodes, ref truncated);
                    if (truncated)
                        return;
                }
            }
        }
    }

    private static void DrainJsonPropertyLines(JsonElement element, Dictionary<string, Queue<int>> propertyLines)
    {
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
        var symbols = new List<SymbolRecord>();
        var truncated = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var match = JsonFallbackPropertyRegex.Match(lines[i]);
            if (!match.Success)
                continue;

            var name = UnescapeJsonPropertyName(match.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (name.Length > StructuredDataMaxPathLength)
                continue;

            if (!TryAddStructuredDataSymbol(fileId, "property", name, i + 1, lines, parentPath: null, symbols, "structured_data_symbol_budget_exceeded", ref truncated))
                break;
        }

        return symbols;
    }

    private static List<SymbolRecord> ExtractYamlSymbols(long fileId, string[] lines)
    {
        var symbols = new List<SymbolRecord>();
        var stack = new List<YamlPathFrame>();
        int? blockScalarIndent = null;
        var traversalNodes = 0;
        var truncated = false;

        for (var i = 0; i < lines.Length; i++)
        {
            if (traversalNodes >= StructuredDataMaxTraversalNodes)
            {
                AddStructuredDataDiagnosticSymbol(symbols, fileId, "structured_data_traversal_budget_exceeded", i + 1, lines, "Structured data traversal exceeded the per-file node budget; remaining nodes were truncated.", ref truncated);
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

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed is "---" or "...")
                continue;

            var match = YamlMappingKeyRegex.Match(line);
            if (!match.Success)
                continue;

            var key = ExtractYamlKey(match);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            while (stack.Count > 0 && indent <= stack[^1].Indent)
                stack.RemoveAt(stack.Count - 1);

            var parentPath = stack.Count == 0 ? null : stack[^1].Path;
            var path = string.IsNullOrEmpty(parentPath) ? key : parentPath + "." + key;
            if (stack.Count >= StructuredDataMaxYamlDepth || path.Length > StructuredDataMaxPathLength)
                continue;

            var value = StripYamlInlineComment(match.Groups["value"].Value).Trim();
            var isContainer = value.Length == 0 || value is "|" or ">" or "|-" or ">-" or "|+" or ">+";
            var kind = isContainer ? "namespace" : "property";

            traversalNodes++;
            if (!TryAddStructuredDataSymbol(fileId, kind, path, i + 1, lines, parentPath, symbols, "structured_data_symbol_budget_exceeded", ref truncated))
                break;

            if (isContainer)
            {
                stack.Add(new YamlPathFrame(indent, path));
                if (value.StartsWith('|') || value.StartsWith('>'))
                    blockScalarIndent = indent;
            }
        }

        return symbols;
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
        ref bool truncated)
    {
        if (symbols.Count < StructuredDataMaxSymbols)
        {
            symbols.Add(CreateStructuredDataSymbol(fileId, kind, name, line, lines, parentPath));
            return true;
        }

        AddStructuredDataDiagnosticSymbol(symbols, fileId, category, line, lines, "Structured data symbol extraction exceeded the per-file symbol budget; remaining symbols were truncated.", ref truncated);
        return false;
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
        string? parentPath)
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
            Signature = lines.Length == 0 ? null : LimitStructuredDataSignature(lines[signatureIndex].Trim()),
            ContainerKind = parentPath == null ? null : "namespace",
            ContainerName = parentPath,
            ContainerQualifiedName = parentPath,
        };
    }

    private static string LimitStructuredDataSignature(string signature) =>
        signature.Length <= StructuredDataMaxSignatureLength
            ? signature
            : signature[..StructuredDataMaxSignatureLength];

    private static Dictionary<string, Queue<int>> BuildJsonPropertyLineQueues(string content)
    {
        var propertyLines = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(content);
        var byteLineStarts = BuildUtf8LineStarts(bytes);
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

    private static int[] BuildUtf8LineStarts(byte[] bytes)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
                starts.Add(i + 1);
        }

        return starts.ToArray();
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
        var encodedName = JsonSerializer.Serialize(propertyName);
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

    private static int[] BuildLineStarts(string[] lines)
    {
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
            return match.Groups["double"].Value.Replace("\"\"", "\"", StringComparison.Ordinal).Trim();
        if (match.Groups["single"].Success)
            return match.Groups["single"].Value.Replace("''", "'", StringComparison.Ordinal).Trim();
        return match.Groups["plain"].Value.Trim();
    }

    private static string StripYamlInlineComment(string value)
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
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + value + "\"") ?? value;
        }
        catch (JsonException)
        {
            return value;
        }
    }
}
