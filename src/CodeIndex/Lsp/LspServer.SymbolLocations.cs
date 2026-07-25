using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Mcp;
using CodeIndex.Models;
using CodeIndex.Security;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer : IDisposable
{
    private JsonObject ToSymbolLocation(SymbolResult symbol, PositionTokenContext context)
    {
        var identifier = GetSymbolIdentifierPosition(symbol, context);
        return ToLocation(
            symbol.Path,
            identifier.Line,
            identifier.StartColumn,
            identifier.Line,
            identifier.EndColumn,
            GetLocationWorkspaceRoot(symbol.Path, context));
    }

    private void AddSymbolLocation(JsonArray array, HashSet<string> seenLocations, SymbolResult symbol, PositionTokenContext context)
    {
        var identifier = GetSymbolIdentifierPosition(symbol, context);
        AddLocation(array, seenLocations, symbol.Path, identifier.Line, identifier.StartColumn, identifier.Line, identifier.EndColumn, context);
    }

    private void AddLocation(
        JsonArray array,
        HashSet<string> seenLocations,
        string path,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        PositionTokenContext context)
    {
        var workspaceRoot = GetLocationWorkspaceRoot(path, context);
        var key = string.Join('\0', PathToUri(path, workspaceRoot ?? _projectRoot), startLine, startColumn, endLine, endColumn);
        if (seenLocations.Add(key))
            array.Add((JsonNode)ToLocation(path, startLine, startColumn, endLine, endColumn, workspaceRoot));
    }

    private string? GetLocationWorkspaceRoot(string path, PositionTokenContext context)
    {
        if (Path.IsPathRooted(path))
            return null;
        return _projectRoot ?? context.WorkspaceRoot;
    }

    private static void RecordLookupFailure(string method, string? failureReason)
    {
        if (string.IsNullOrEmpty(failureReason))
            return;

        Activity.Current?.AddEvent(new ActivityEvent(
            LspLookupFailureEventName,
            tags: new ActivityTagsCollection
            {
                [LspMethodTag] = method,
                [LspLookupFailureReasonTag] = failureReason,
            }));
    }

    private DocumentSymbolNode? FindDocumentSymbolParent(IReadOnlyList<DocumentSymbolNode> nodes, int symbolIndex)
    {
        var symbolNode = nodes[symbolIndex];
        var symbol = symbolNode.Symbol;
        DocumentSymbolNode? parent = null;
        int? nearestSameLineStart = null;
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (i == symbolIndex)
                continue;

            var candidate = nodes[i].Symbol;
            if (!ContainsDocumentSymbol(candidate, symbol))
                continue;
            if (symbol.ContainerName != null
                && !string.Equals(candidate.Name, symbol.ContainerName, StringComparison.Ordinal))
            {
                continue;
            }
            if (symbol.ContainerKind != null
                && !string.Equals(candidate.Kind, symbol.ContainerKind, StringComparison.Ordinal))
            {
                continue;
            }

            ConsiderDocumentSymbolParent(nodes[i], symbolNode, ref parent, ref nearestSameLineStart);
        }

        if (parent != null)
            return parent;
        if (symbol.ContainerName != null)
            return null;

        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (i == symbolIndex)
                continue;

            var candidate = nodes[i].Symbol;
            if (ContainsDocumentSymbol(candidate, symbol))
                ConsiderDocumentSymbolParent(nodes[i], symbolNode, ref parent, ref nearestSameLineStart);
        }

        return parent;
    }

    private static void ConsiderDocumentSymbolParent(
        DocumentSymbolNode candidate,
        DocumentSymbolNode symbol,
        ref DocumentSymbolNode? parent,
        ref int? nearestSameLineStart)
    {
        if (candidate.Symbol.StartLine == symbol.Symbol.StartLine)
        {
            var candidateStart = candidate.Item["selectionRange"]?["start"]?["character"]?.GetValue<int>();
            var symbolStart = symbol.Item["selectionRange"]?["start"]?["character"]?.GetValue<int>();
            if (candidateStart.HasValue && symbolStart.HasValue)
            {
                if (candidateStart.Value > symbolStart.Value)
                    return;
                if (!nearestSameLineStart.HasValue || candidateStart.Value > nearestSameLineStart.Value)
                {
                    parent = candidate;
                    nearestSameLineStart = candidateStart.Value;
                }
                return;
            }
        }

        if (!nearestSameLineStart.HasValue && parent == null)
            parent = candidate;
    }

    private static bool ContainsDocumentSymbol(SymbolResult candidate, SymbolResult symbol) =>
        candidate.StartLine <= symbol.StartLine
        && candidate.EndLine >= symbol.EndLine
        && (candidate.StartLine < symbol.StartLine
            || candidate.EndLine > symbol.EndLine
            || (symbol.ContainerName != null
                && symbol.ContainerKind != null
                && string.Equals(candidate.Name, symbol.ContainerName, StringComparison.Ordinal)
                && string.Equals(candidate.Kind, symbol.ContainerKind, StringComparison.Ordinal)));

    private static void AddDocumentSymbolChild(JsonObject parent, JsonObject child)
    {
        if (parent["children"] is not JsonArray children)
        {
            children = [];
            parent["children"] = children;
        }

        children.Add((JsonNode)child);
    }

    private int TrimDocumentSymbolsToBudget(JsonArray roots)
    {
        var removedCount = 0;
        var responseBudget = DocumentSymbolResponseBytesForTesting ?? MaxDocumentSymbolResponseBytes;
        var responseBytes = MeasureJsonUtf8Bytes(roots);
        while (roots.Count > 0 && responseBytes > responseBudget)
        {
            if (!RemoveLastDocumentSymbol(roots, out var removedBytes))
                break;
            removedCount++;

            if (_jsonOptions.WriteIndented)
                responseBytes = MeasureJsonUtf8Bytes(roots);
            else
                responseBytes = removedBytes > 0
                    ? Math.Max(0, responseBytes - removedBytes)
                    : MeasureJsonUtf8Bytes(roots);
        }

        return removedCount;
    }

    private bool RemoveLastDocumentSymbol(JsonArray symbols, out int removedBytes)
    {
        removedBytes = 0;
        if (symbols.Count == 0)
            return false;

        if (symbols[symbols.Count - 1] is JsonObject last
            && last["children"] is JsonArray children
            && children.Count > 0)
        {
            var beforeBytes = MeasureJsonUtf8Bytes(last);
            if (RemoveLastDocumentSymbol(children, out _))
            {
                if (children.Count == 0)
                    last.Remove("children");
                removedBytes = Math.Max(0, beforeBytes - MeasureJsonUtf8Bytes(last));
                return true;
            }
        }

        removedBytes = MeasureJsonUtf8Bytes(symbols[symbols.Count - 1])
            + (symbols.Count > 1 ? 1 : 0);
        symbols.RemoveAt(symbols.Count - 1);
        return true;
    }

    private int MeasureJsonUtf8Bytes(JsonNode? node)
    {
        if (node == null)
            return "null"u8.Length;
        if (_jsonOptions.WriteIndented)
            return Encoding.UTF8.GetByteCount(node.ToJsonString(_jsonOptions));

        if (node is JsonArray array)
        {
            var bytes = "[]"u8.Length;
            for (var i = 0; i < array.Count; i++)
            {
                if (i > 0)
                    bytes++;
                bytes += MeasureJsonUtf8Bytes(array[i]);
            }
            return bytes;
        }

        if (node is JsonObject obj)
        {
            var bytes = "{}"u8.Length;
            var propertyIndex = 0;
            foreach (var property in obj)
            {
                if (propertyIndex > 0)
                    bytes++;
                bytes += MeasureJsonStringUtf8Bytes(property.Key);
                bytes++;
                bytes += MeasureJsonUtf8Bytes(property.Value);
                propertyIndex++;
            }
            return bytes;
        }

        return Encoding.UTF8.GetByteCount(node.ToJsonString(_jsonOptions));
    }

    private int MeasureJsonStringUtf8Bytes(string value) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, _jsonOptions));

}
