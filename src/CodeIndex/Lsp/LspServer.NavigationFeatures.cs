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
    private JsonArray Definition(JsonElement root, string method)
    {
        if (!TryExtractPositionToken(root, out var context, out var failureReason))
        {
            RecordLookupFailure(method, failureReason);
            return [];
        }

        var definitions = ResolveLspDefinitions(context);
        var array = new JsonArray();
        foreach (var definition in definitions)
            array.Add((JsonNode)ToSymbolLocation(definition, context));
        return array;
    }

    private JsonArray References(JsonElement root, string method)
    {
        if (!TryExtractPositionToken(root, out var context, out var failureReason))
        {
            RecordLookupFailure(method, failureReason);
            return [];
        }

        var includeDeclaration = GetBool(root, "params", "context", "includeDeclaration") == true;
        var references = ResolveLspReferences(context);
        var array = new JsonArray();
        var seenLocations = new HashSet<string>(StringComparer.Ordinal);
        if (includeDeclaration)
        {
            foreach (var definition in ResolveLspDefinitions(context))
                AddSymbolLocation(array, seenLocations, definition, context);
        }

        foreach (var reference in references)
            AddLocation(
                array,
                seenLocations,
                reference.Path,
                reference.Line,
                Math.Max(reference.Column, 1),
                reference.Line,
                Math.Max(reference.Column, 1) + Math.Max(context.Token.Length, 1),
                context);
        return array;
    }

    private JsonNode? Hover(JsonElement root, string method)
    {
        if (!TryExtractPositionToken(root, out var context, out var failureReason))
        {
            RecordLookupFailure(method, failureReason);
            return null;
        }

        var definition = ResolveLspDefinitions(context).FirstOrDefault();
        if (definition == null)
            return null;

        return new JsonObject
        {
            ["contents"] = new JsonObject
            {
                ["kind"] = "plaintext",
                ["value"] = FormatHoverText(definition),
            },
            ["range"] = ToRange(context.Line + 1, context.StartCharacter + 1, context.Line + 1, context.EndCharacter + 1),
        };
    }

    private JsonObject Completion(JsonElement root, string method)
    {
        if (!TryExtractPositionToken(root, out var context, out var failureReason))
        {
            RecordLookupFailure(method, failureReason);
            return CompletionList([]);
        }

        var symbols = _reader.SearchSymbols(context.Token, MaxCompletionItems, pathPatterns: [context.IndexedPath])
            .Concat(_reader.SearchSymbols(context.Token, MaxCompletionItems))
            .DistinctBy(BuildCompletionIdentity)
            .Take(MaxCompletionItems)
            .ToList();
        var items = new JsonArray();
        for (var i = 0; i < symbols.Count; i++)
            items.Add((JsonNode)ToCompletionItem(symbols[i], i));
        return CompletionList(items);
    }

    private JsonArray DocumentHighlight(JsonElement root, string method)
    {
        if (!TryExtractPositionToken(root, out var context, out var failureReason))
        {
            RecordLookupFailure(method, failureReason);
            return [];
        }

        var array = new JsonArray();
        var seenRanges = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in ResolveLspDefinitions(context).Where(definition => string.Equals(definition.Path, context.IndexedPath, StringComparison.Ordinal)))
        {
            var identifier = GetSymbolIdentifierPosition(definition, context.ResolvedPath);
            AddDocumentHighlight(array, seenRanges, identifier.Line, identifier.StartColumn, identifier.Line, identifier.EndColumn);
        }

        foreach (var reference in ResolveLspReferences(context).Where(reference => string.Equals(reference.Path, context.IndexedPath, StringComparison.Ordinal)))
        {
            var startColumn = Math.Max(reference.Column, 1);
            AddDocumentHighlight(array, seenRanges, reference.Line, startColumn, reference.Line, startColumn + Math.Max(context.Token.Length, 1));
        }

        if (array.Count == 0)
            AddDocumentHighlight(array, seenRanges, context.Line + 1, context.StartCharacter + 1, context.Line + 1, context.EndCharacter + 1);
        return array;
    }

    private JsonObject SemanticTokensFull(JsonElement root)
    {
        if (!TryResolveIndexedDocument(root, out var document))
            return new JsonObject { ["data"] = new JsonArray() };

        var isCSharp = string.Equals(
            Path.GetExtension(document.ResolvedPath),
            ".cs",
            StringComparison.OrdinalIgnoreCase);
        var symbols = isCSharp
            ? BuildCSharpSemanticTokens(document).ToList()
            : BuildIndexedSemanticTokens(document);
        symbols = symbols
            .OrderBy(token => token.Line)
            .ThenBy(token => token.StartCharacter)
            .ToList();
        var data = new JsonArray();
        var previousLine = 0;
        var previousStart = 0;
        foreach (var token in symbols)
        {
            var deltaLine = token.Line - previousLine;
            var deltaStart = deltaLine == 0 ? token.StartCharacter - previousStart : token.StartCharacter;
            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(token.Length);
            data.Add(token.TokenType);
            data.Add(token.TokenModifiers);
            previousLine = token.Line;
            previousStart = token.StartCharacter;
        }

        return new JsonObject { ["data"] = data };
    }

    private List<SemanticToken> BuildIndexedSemanticTokens(IndexedDocumentContext document)
    {
        var lineCache = new Dictionary<int, string?>();
        return GetDocumentSymbols(document.IndexedPath, MaxSemanticTokenItems)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol.Name))
            .Take(MaxSemanticTokenItems)
            .Select(symbol => BuildSemanticToken(document, symbol, lineCache))
            .Where(token => token.HasValue)
            .Select(token => token!.Value)
            .OrderBy(token => token.Line)
            .ThenBy(token => token.StartCharacter)
            .ToList();
    }

    private JsonArray InlayHint(JsonElement root)
    {
        var requestedRange = ReadRequiredLspRange(root, "params", "range");
        if (!TryResolveIndexedDocument(root, out var document))
            return [];

        var array = new JsonArray();
        var lineCache = new Dictionary<int, string?>();
        foreach (var symbol in GetDocumentSymbols(
                document.IndexedPath,
                MaxDocumentSymbols,
                ToOneBasedLspLine(requestedRange.Start.Line),
                ToOneBasedLspLine(requestedRange.End.Line))
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol.ReturnType))
            .Where(symbol => IsInlayHintInRequestedRange(requestedRange, document, symbol, lineCache))
            .Where(symbol => !HasExplicitTypeBeforeSymbol(document, symbol, lineCache))
            .Take(MaxInlayHintItems))
        {
            array.Add((JsonNode)ToInlayHint(document, symbol, lineCache));
        }
        return array;
    }

    private bool IsInlayHintInRequestedRange(
        LspRange requestedRange,
        IndexedDocumentContext document,
        SymbolResult symbol,
        Dictionary<int, string?> lineCache)
    {
        var line = Math.Max(symbol.Line, 1) - 1;
        var character = FindSymbolStartCharacter(document.ResolvedPath, symbol, lineCache) + symbol.Name.Length;
        return IsPositionInRange(
            line,
            character,
            requestedRange.Start.Line,
            requestedRange.Start.Character,
            requestedRange.End.Line,
            requestedRange.End.Character);
    }

    private bool HasExplicitTypeBeforeSymbol(
        IndexedDocumentContext document,
        SymbolResult symbol,
        Dictionary<int, string?> lineCache)
    {
        var line = Math.Max(symbol.Line, symbol.StartLine);
        if (string.IsNullOrWhiteSpace(symbol.ReturnType) ||
            line <= 0 ||
            !TryReadPositionLineCached(document.ResolvedPath, line - 1, lineCache, out var sourceLine))
        {
            return false;
        }

        var symbolStart = FindSymbolStartCharacter(document.ResolvedPath, symbol, lineCache);
        if (symbolStart <= 0 || sourceLine.Length == 0)
            return false;

        var typeStart = sourceLine.LastIndexOf(symbol.ReturnType, symbolStart - 1, StringComparison.Ordinal);
        if (typeStart < 0)
            return false;

        var typeEnd = typeStart + symbol.ReturnType.Length;
        return typeEnd <= symbolStart && sourceLine.AsSpan(typeEnd, symbolStart - typeEnd).Trim().IsEmpty;
    }

    private static bool IsPositionInRange(
        int line,
        int character,
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter)
        => ComparePosition(line, character, startLine, startCharacter) >= 0 &&
           ComparePosition(line, character, endLine, endCharacter) < 0;

    private static JsonObject CompletionList(JsonArray items) => new()
    {
        ["isIncomplete"] = false,
        ["items"] = items,
    };

    private static string BuildCompletionIdentity(SymbolResult symbol)
        => string.Join('\0', symbol.Name, symbol.Kind, symbol.Path, symbol.Line.ToString(CultureInfo.InvariantCulture));

    private static JsonObject ToCompletionItem(SymbolResult symbol, int index) => new()
    {
        ["label"] = symbol.Name,
        ["kind"] = CompletionItemKind(symbol.Kind),
        ["detail"] = FormatSymbolDetail(symbol),
        ["sortText"] = index.ToString("D4", CultureInfo.InvariantCulture) + "_" + symbol.Name,
    };

    private string FormatHoverText(SymbolResult symbol)
    {
        var builder = new StringBuilder();
        builder.Append(symbol.Kind).Append(' ').Append(symbol.Name);
        if (!string.IsNullOrWhiteSpace(symbol.Signature))
            builder.AppendLine().Append(symbol.Signature);
        builder.AppendLine().Append(FormatHoverPath(symbol.Path)).Append(':').Append(symbol.Line.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(symbol.ContainerName))
            builder.AppendLine().Append("container: ").Append(symbol.ContainerName);
        if (!string.IsNullOrWhiteSpace(symbol.ReturnType))
            builder.AppendLine().Append("returns: ").Append(symbol.ReturnType);
        return builder.ToString();
    }

    private string FormatHoverPath(string path)
    {
        if (!Path.IsPathRooted(path))
            return path.Replace('\\', '/');

        foreach (var root in EnumerateHoverRoots())
        {
            if (TryGetRelativePath(root, path, out var relativePath) && relativePath != null)
                return relativePath.Replace('\\', '/');
        }

        return "[outside workspace]";
    }

    private IEnumerable<string> EnumerateHoverRoots()
    {
        if (_projectRoot != null)
            yield return _projectRoot;
        foreach (var workspaceFolder in _workspaceFolders)
            yield return workspaceFolder;
    }

    private static string FormatSymbolDetail(SymbolResult symbol)
    {
        var detail = string.IsNullOrWhiteSpace(symbol.Signature)
            ? $"{symbol.Kind} {symbol.Path}:{symbol.Line.ToString(CultureInfo.InvariantCulture)}"
            : symbol.Signature;
        return detail.Length <= MaxDocumentSymbolDetailChars
            ? detail
            : detail[..(MaxDocumentSymbolDetailChars - "...".Length)] + "...";
    }

    private static int CompletionItemKind(string kind) => kind switch
    {
        "class" => 7,
        "function" or "test.method" => 3,
        "property" => 10,
        "field" => 5,
        "enum" => 13,
        "interface" => 8,
        "namespace" => 9,
        "struct" => 22,
        _ => 6,
    };

    private static void AddDocumentHighlight(JsonArray array, HashSet<string> seenRanges, int startLine, int startColumn, int endLine, int endColumn)
    {
        var key = string.Join('\0', startLine, startColumn, endLine, endColumn);
        if (!seenRanges.Add(key))
            return;

        array.Add(new JsonObject
        {
            ["range"] = ToRange(startLine, startColumn, endLine, endColumn),
            ["kind"] = 1,
        });
    }

    private JsonObject ToInlayHint(IndexedDocumentContext document, SymbolResult symbol, Dictionary<int, string?> lineCache)
    {
        var startCharacter = FindSymbolStartCharacter(document.ResolvedPath, symbol, lineCache);
        return new JsonObject
        {
            ["position"] = ToPosition(symbol.Line, startCharacter + symbol.Name.Length + 1),
            ["label"] = ": " + symbol.ReturnType,
            ["kind"] = 1,
            ["paddingLeft"] = true,
        };
    }

}
