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
using CodeIndex.Indexer;
using CodeIndex.Mcp;
using CodeIndex.Models;
using CodeIndex.Security;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer : IDisposable
{
    private JsonObject BuildInitializeResult() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["definitionProvider"] = true,
            ["declarationProvider"] = true,
            ["referencesProvider"] = true,
            ["documentSymbolProvider"] = new JsonObject
            {
                ["workDoneProgress"] = true,
            },
            ["workspaceSymbolProvider"] = new JsonObject
            {
                ["workDoneProgress"] = true,
            },
            ["hoverProvider"] = true,
            ["completionProvider"] = new JsonObject
            {
                ["resolveProvider"] = false,
                ["triggerCharacters"] = new JsonArray(".", ":", "_"),
            },
            ["documentHighlightProvider"] = true,
            ["semanticTokensProvider"] = new JsonObject
            {
                ["legend"] = new JsonObject
                {
                    ["tokenTypes"] = ToJsonStringArray(SemanticTokenTypes),
                    ["tokenModifiers"] = ToJsonStringArray(SemanticTokenModifiers),
                },
                ["full"] = true,
                ["range"] = false,
            },
            ["inlayHintProvider"] = new JsonObject
            {
                ["resolveProvider"] = false,
            },
            ["textDocumentSync"] = new JsonObject
            {
                ["openClose"] = true,
                ["change"] = 1,
            },
            ["workspace"] = new JsonObject
            {
                ["workspaceFolders"] = new JsonObject
                {
                    ["supported"] = true,
                    ["changeNotifications"] = true,
                },
            },
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "cdidx",
            ["version"] = _version,
        },
    };

    private static JsonArray ToJsonStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private JsonArray WorkspaceSymbol(JsonElement root) =>
        CreateWorkspaceSymbolResponse(
            root,
            createPartialItems: false,
            CancellationToken.None).FinalItems;

    private SymbolResponse CreateWorkspaceSymbolResponse(
        JsonElement root,
        bool createPartialItems,
        CancellationToken cancellationToken)
    {
        var query = GetString(root, "params", "query");
        if (query != null && query.Length > QueryLimits.MaxQueryLength)
            throw new ArgumentException(QueryLimits.FormatQueryTooLongError());

        var limit = GetLimit(root, DefaultLimit, MaxWorkspaceSymbols, "params", "limit")
            ?? GetLimit(root, DefaultLimit, MaxWorkspaceSymbols, "params", "maxResults")
            ?? DefaultLimit;
        IReadOnlyList<SymbolResult> candidates = limit == 0
            ? []
            : _reader.SearchSymbols(query, checked(limit + 1));
        var truncated = candidates.Count > limit;
        var symbols = candidates.Take(limit).ToList();
        if (createPartialItems)
        {
            return new SymbolResponse(
                [],
                EnumerateWorkspaceSymbolItems(symbols, cancellationToken),
                symbols.Count,
                truncated);
        }

        var identifiers = new (int Line, int StartColumn, int EndColumn)[symbols.Count];
        var pathComparer = _pathStringComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        foreach (var pathGroup in symbols
            .Select((symbol, index) => (Symbol: symbol, Index: index))
            .GroupBy(item => item.Symbol.Path, pathComparer))
        {
            var resolvedPath = TryResolveIndexedFilePath(pathGroup.Key, out var path) ? path : null;
            var lineCache = new Dictionary<int, string?>();
            foreach (var item in pathGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                identifiers[item.Index] = GetSymbolIdentifierPosition(item.Symbol, resolvedPath, lineCache);
            }
        }

        var array = new JsonArray();
        for (var index = 0; index < symbols.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            array.Add((JsonNode)ToWorkspaceSymbol(symbols[index], identifiers[index]));
        }

        return new SymbolResponse(array, [], array.Count, truncated);
    }

    private IEnumerable<JsonNode> EnumerateWorkspaceSymbolItems(
        IReadOnlyList<SymbolResult> symbols,
        CancellationToken cancellationToken)
    {
        var pathComparer = _pathStringComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var pathContexts = new Dictionary<
            string,
            (string? ResolvedPath, Dictionary<int, string?> LineCache)>(pathComparer);
        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pathContexts.TryGetValue(symbol.Path, out var context))
            {
                context = (
                    TryResolveIndexedFilePath(symbol.Path, out var path) ? path : null,
                    new Dictionary<int, string?>());
                pathContexts.Add(symbol.Path, context);
            }

            var identifier = GetSymbolIdentifierPosition(
                symbol,
                context.ResolvedPath,
                context.LineCache);
            yield return ToWorkspaceSymbol(symbol, identifier);
        }
    }

    private JsonArray DocumentSymbol(JsonElement root) =>
        CreateDocumentSymbolResponse(
            root,
            createPartialItems: false,
            CancellationToken.None).FinalItems;

    private SymbolResponse CreateDocumentSymbolResponse(
        JsonElement root,
        bool createPartialItems,
        CancellationToken cancellationToken)
    {
        if (!TryResolveIndexedDocument(root, out var document))
            return new SymbolResponse([], [], 0, false);

        var candidates = GetDocumentSymbolCandidates(document, cancellationToken);
        var materializationTruncated = candidates.Count > MaxDocumentSymbolMaterialization;
        var materializedCount = Math.Min(candidates.Count, MaxDocumentSymbolMaterialization);
        Activity.Current?.SetTag("lsp.document_symbols.materialized_count", materializedCount);
        Activity.Current?.SetTag("lsp.document_symbols.materialization_truncated", materializationTruncated);

        var symbols = candidates
            .Take(MaxDocumentSymbolMaterialization)
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .ThenBy(s => s.ContainerName == null ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        if (createPartialItems)
        {
            Activity.Current?.SetTag("lsp.document_symbols.returned_root_count", 0);
            Activity.Current?.SetTag("lsp.document_symbols.returned_partial_count", symbols.Count);
            return new SymbolResponse(
                [],
                EnumerateDocumentSymbolItems(document, symbols, cancellationToken),
                symbols.Count,
                materializationTruncated);
        }

        var tree = BuildDocumentSymbolTree(document, symbols, cancellationToken);
        Activity.Current?.SetTag("lsp.document_symbols.returned_root_count", tree.Roots.Count);
        return new SymbolResponse(
            tree.Roots,
            [],
            Math.Max(0, symbols.Count - tree.RemovedCount),
            materializationTruncated || tree.RemovedCount > 0);
    }

    private IReadOnlyList<SymbolResult> GetDocumentSymbolCandidates(
        IndexedDocumentContext document,
        CancellationToken cancellationToken)
    {
        if (!_liveDocumentStore.TryGetText(document.ResolvedPath, out var liveText))
        {
            return _reader.SearchSymbols(
                (string?)null,
                MaxDocumentSymbolMaterialization + 1,
                pathPatterns: [document.IndexedPath]);
        }

        var language = _reader.GetFileByPath(document.IndexedPath)?.Lang;
        if (string.IsNullOrWhiteSpace(language)
            || !SymbolExtractor.TryExtractBounded(
                0,
                language,
                liveText,
                MaxDocumentSymbolMaterialization + 1,
                document.ResolvedPath,
                _projectRoot ?? document.WorkspaceRoot,
                cancellationToken,
                out var liveSymbols))
        {
            return _reader.SearchSymbols(
                (string?)null,
                MaxDocumentSymbolMaterialization + 1,
                pathPatterns: [document.IndexedPath]);
        }

        return liveSymbols
            .Select(symbol => new SymbolResult
            {
                Path = document.IndexedPath,
                Lang = language,
                Kind = symbol.Kind,
                SubKind = symbol.SubKind,
                Name = symbol.Name,
                Line = symbol.Line,
                StartLine = symbol.StartLine,
                StartColumn = symbol.StartColumn,
                EndLine = symbol.EndLine,
                BodyStartLine = symbol.BodyStartLine,
                BodyEndLine = symbol.BodyEndLine,
                Signature = symbol.Signature,
                ContainerKind = symbol.ContainerKind,
                ContainerName = symbol.ContainerName,
                ContainerQualifiedName = symbol.ContainerQualifiedName,
                Visibility = symbol.Visibility,
                ReturnType = symbol.ReturnType,
            })
            .ToList();
    }

    private IEnumerable<JsonNode> EnumerateDocumentSymbolItems(
        IndexedDocumentContext document,
        IReadOnlyList<SymbolResult> symbols,
        CancellationToken cancellationToken)
    {
        var lineCache = new Dictionary<int, string?>();
        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToDocumentSymbolInformation(document, symbol, lineCache);
        }
    }

    private DocumentSymbolTreeResult BuildDocumentSymbolTree(
        IndexedDocumentContext document,
        IReadOnlyList<SymbolResult> symbols,
        CancellationToken cancellationToken)
    {
        var roots = new JsonArray();
        var nodes = new List<DocumentSymbolNode>(symbols.Count);
        var lineCache = new Dictionary<int, string?>();
        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = ToDocumentSymbol(document, symbol, lineCache);
            nodes.Add(new DocumentSymbolNode(symbol, item));
        }

        for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = nodes[nodeIndex];
            var parent = FindDocumentSymbolParent(nodes, nodeIndex);
            if (parent == null)
                roots.Add((JsonNode)node.Item);
            else
                AddDocumentSymbolChild(parent.Value.Item, node.Item);
        }

        return new DocumentSymbolTreeResult(roots, TrimDocumentSymbolsToBudget(roots));
    }

    private JsonObject HandleSymbolRequest(
        JsonNode? id,
        JsonElement root,
        bool documentSymbols,
        Action<JsonObject>? outbound,
        CancellationToken cancellationToken)
    {
        var partialResultToken = GetProgressToken(root, "partialResultToken");
        var workDoneToken = GetProgressToken(root, "workDoneToken");
        if (outbound == null)
            return Result(id, documentSymbols ? DocumentSymbol(root) : WorkspaceSymbol(root));

        var title = documentSymbols ? "CodeIndex document symbols" : "CodeIndex workspace symbols";
        if (workDoneToken != null)
            outbound(CreateProgressNotification(workDoneToken, CreateWorkDoneBegin(title)));

        var emittedCount = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeSymbolRequestForTesting?.Invoke(cancellationToken);
            var response = documentSymbols
                ? CreateDocumentSymbolResponse(
                    root,
                    createPartialItems: partialResultToken != null,
                    cancellationToken)
                : CreateWorkspaceSymbolResponse(
                    root,
                    createPartialItems: partialResultToken != null,
                    cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var truncated = response.Truncated;
            JsonNode? finalResult = response.FinalItems;
            if (partialResultToken != null)
            {
                var emission = EmitPartialResultChunks(
                    outbound,
                    partialResultToken,
                    workDoneToken,
                    response.PartialItems,
                    response.ReturnedCount,
                    cancellationToken);
                emittedCount = emission.EmittedCount;
                truncated |= emission.Truncated;
                if (emission.Cancelled)
                {
                    return CompleteCancelledSymbolRequest(
                        id,
                        outbound,
                        workDoneToken,
                        emittedCount);
                }

                finalResult = null;
            }
            else
            {
                emittedCount = response.ReturnedCount;
                if (workDoneToken != null)
                {
                    outbound(CreateProgressNotification(
                        workDoneToken,
                        CreateWorkDoneReport(100, $"Prepared {emittedCount} symbols.")));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var summary = CreateSymbolProgressSummary(emittedCount, truncated);
            if (workDoneToken != null)
                outbound(CreateProgressNotification(workDoneToken, CreateWorkDoneEnd(summary)));
            else if (truncated)
                outbound(CreateLogMessage(summary));

            return Result(id, finalResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CompleteCancelledSymbolRequest(
                id,
                outbound,
                workDoneToken,
                emittedCount);
        }
        catch
        {
            if (workDoneToken != null)
            {
                outbound(CreateProgressNotification(
                    workDoneToken,
                    CreateWorkDoneEnd("Symbol request failed.")));
            }

            throw;
        }
    }

    private PartialResultEmission EmitPartialResultChunks(
        Action<JsonObject> outbound,
        JsonNode partialResultToken,
        JsonNode? workDoneToken,
        IEnumerable<JsonNode> items,
        int totalCount,
        CancellationToken cancellationToken)
    {
        var emittedCount = 0;
        var chunk = new JsonArray();
        try
        {
            foreach (var item in items)
            {
                if (cancellationToken.IsCancellationRequested)
                    return new PartialResultEmission(emittedCount, false, true);

                chunk.Add(item);
                var measuredNotification = CreateProgressNotification(
                    partialResultToken,
                    chunk.DeepClone());
                var exceedsChunkBudget = chunk.Count > MaxSymbolProgressChunkItems
                    || MeasureJsonUtf8Bytes(measuredNotification) > MaxSymbolProgressChunkBytes;
                if (exceedsChunkBudget)
                {
                    chunk.RemoveAt(chunk.Count - 1);
                    if (chunk.Count > 0)
                    {
                        EmitPartialResultChunk(
                            outbound,
                            partialResultToken,
                            workDoneToken,
                            chunk,
                            ref emittedCount,
                            totalCount);
                    }

                    chunk = [];
                    chunk.Add(item);
                    measuredNotification = CreateProgressNotification(
                        partialResultToken,
                        chunk.DeepClone());
                    if (MeasureJsonUtf8Bytes(measuredNotification) > MaxSymbolProgressChunkBytes)
                        return new PartialResultEmission(emittedCount, true, false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new PartialResultEmission(emittedCount, false, true);
        }

        if (chunk.Count > 0)
        {
            EmitPartialResultChunk(
                outbound,
                partialResultToken,
                workDoneToken,
                chunk,
                ref emittedCount,
                totalCount);
        }
        else if (totalCount == 0 && workDoneToken != null)
        {
            outbound(CreateProgressNotification(
                workDoneToken,
                CreateWorkDoneReport(100, "Prepared 0 symbols.")));
        }

        return new PartialResultEmission(emittedCount, false, cancellationToken.IsCancellationRequested);
    }

    private static void EmitPartialResultChunk(
        Action<JsonObject> outbound,
        JsonNode partialResultToken,
        JsonNode? workDoneToken,
        JsonArray chunk,
        ref int emittedCount,
        int totalCount)
    {
        var chunkCount = chunk.Count;
        outbound(CreateProgressNotification(partialResultToken, chunk));
        emittedCount += chunkCount;
        if (workDoneToken == null)
            return;

        var percentage = totalCount == 0
            ? 100
            : Math.Clamp((int)((long)emittedCount * 100 / totalCount), 0, 100);
        outbound(CreateProgressNotification(
            workDoneToken,
            CreateWorkDoneReport(percentage, $"Streamed {emittedCount} symbols.")));
    }

    private static JsonObject CompleteCancelledSymbolRequest(
        JsonNode? id,
        Action<JsonObject> outbound,
        JsonNode? workDoneToken,
        int emittedCount)
    {
        if (workDoneToken != null)
        {
            outbound(CreateProgressNotification(
                workDoneToken,
                CreateWorkDoneEnd($"Cancelled after {emittedCount} symbols.")));
        }

        return Error(id, JsonRpcRequestCancelledCode, JsonRpcRequestCancelledMessage);
    }

    private static JsonNode? GetProgressToken(JsonElement root, string propertyName)
    {
        if (!TryGet(root, out var token, "params", propertyName))
            return null;

        if (token.ValueKind == JsonValueKind.String)
        {
            var value = token.GetString() ?? string.Empty;
            if (value.Length <= MaxRequestIdStringChars)
                return JsonValue.Create(value);
        }
        else if (token.ValueKind == JsonValueKind.Number && token.TryGetInt64(out var integer))
        {
            return JsonValue.Create(integer);
        }

        throw new ArgumentException($"{propertyName} must be a bounded string or integer.");
    }

    private static JsonObject CreateProgressNotification(JsonNode token, JsonNode? value) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "$/progress",
        ["params"] = new JsonObject
        {
            ["token"] = token.DeepClone(),
            ["value"] = value,
        },
    };

    private static JsonObject CreateWorkDoneBegin(string title) => new()
    {
        ["kind"] = "begin",
        ["title"] = title,
        ["cancellable"] = true,
        ["percentage"] = 0,
    };

    private static JsonObject CreateWorkDoneReport(int percentage, string message) => new()
    {
        ["kind"] = "report",
        ["percentage"] = percentage,
        ["message"] = message,
    };

    private static JsonObject CreateWorkDoneEnd(string message) => new()
    {
        ["kind"] = "end",
        ["message"] = message,
    };

    private static JsonObject CreateLogMessage(string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["method"] = "window/logMessage",
        ["params"] = new JsonObject
        {
            ["type"] = 2,
            ["message"] = message,
        },
    };

    private static string CreateSymbolProgressSummary(int returnedCount, bool truncated) =>
        truncated
            ? $"Returned {returnedCount} symbols; truncated at a configured result or progress-frame limit."
            : $"Returned {returnedCount} symbols.";

}
