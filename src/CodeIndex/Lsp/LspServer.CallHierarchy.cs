using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer
{
    internal const int MaxCallHierarchySites = 1000;
    internal const int MaxCallHierarchyItems = 100;
    internal const int MaxCallHierarchyResponseBytes = 512 * 1024;
    private const int MaxCallHierarchyDataChars = 160;
    private readonly string _callHierarchySession = Guid.NewGuid().ToString("N");
    internal Action<CancellationToken>? BeforeCallHierarchyForTesting { get; set; }
    internal Action? BeforeCallHierarchyValidationForTesting { get; set; }
    private const string CallHierarchyNotice =
        "CodeIndex call hierarchy shows resolved indexed call evidence, not compiler-complete calls. "
        + "External, dynamic and unindexed calls may be absent, including in empty results.";

    private sealed class CallHierarchyException(string reason, bool changed = false) : Exception(reason)
    {
        internal bool Changed { get; } = changed;
    }

    private sealed class CallHierarchyRead(string generation)
    {
        internal string Generation { get; } = generation;
        internal Dictionary<string, (string FullPath, string Checksum, IReadOnlyList<string?> Lines)> Files { get; } = new(StringComparer.Ordinal);
        internal long SourceBytes { get; set; }
    }

    private JsonObject HandleCallHierarchy(
        JsonNode? id, JsonElement root, string method, Action<JsonObject>? outbound,
        CancellationToken requestCancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, _reader.Cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var cancellationScope = _reader.BeginCancellationScope(deadline.Token);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            BeforeCallHierarchyForTesting?.Invoke(deadline.Token);
            var result = _reader.RunWithCancellationInterrupt(() =>
            {
                var read = new CallHierarchyRead(_reader.GetCallHierarchyGeneration());
                var readiness = _reader.GetPersistedIndexGenerationReadiness();
                if (!readiness.GraphDataCurrent || !readiness.ReferenceGraphComplete
                    || !readiness.IndexComplete || !_reader.CallHierarchyIdentityAvailable)
                    throw new CallHierarchyException("graph_unavailable_or_incomplete");
                if (_liveDocumentStore.HasDiscardedText)
                    throw new CallHierarchyException("live_document_evicted_reconnect_required", changed: true);

                // A dirty caller in another open document can change even a zero incoming result.
                foreach (var document in _liveDocumentStore.Documents)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!TryResolveDocumentPath(document.Key, out var fullPath, out var relativePath, out var workspaceRoot))
                        continue;
                    var indexedPath = ResolveIndexedPath(document.Key, fullPath, relativePath, workspaceRoot);
                    if (indexedPath == null)
                        throw new CallHierarchyException("open_document_not_indexed", changed: true);
                    ReadCallHierarchyFile(indexedPath, read);
                }

                JsonNode? items = method == "textDocument/prepareCallHierarchy"
                    ? PrepareCallHierarchy(root, read)
                    : ExpandCallHierarchy(root, method == "callHierarchy/incomingCalls", read);
                BeforeCallHierarchyValidationForTesting?.Invoke();
                deadline.Token.ThrowIfCancellationRequested();
                // Recheck the bounded source set after assembling ranges to catch concurrent saves.
                foreach (var file in read.Files.Values)
                {
                    if (!FileIndexer.TryComputeChecksum(file.FullPath, MaxPositionDocumentBytes, out var checksum, deadline.Token)
                        || checksum != file.Checksum)
                        throw new CallHierarchyException("document_changed", changed: true);
                }
                if (read.Generation != _reader.GetCallHierarchyGeneration()
                    || (_ownedQueryDb != null && !_ownedQueryDb.IsQueryOnlySnapshotCurrent()))
                    throw new CallHierarchyException("index_generation_changed", changed: true);
                var response = Result(id, items);
                if (Encoding.UTF8.GetByteCount(response.ToJsonString(_jsonOptions)) > MaxCallHierarchyResponseBytes)
                    throw new CallHierarchyException("response_budget_exceeded");
                return response;
            });
            outbound?.Invoke(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "window/logMessage",
                ["params"] = new JsonObject { ["type"] = 3, ["message"] = CallHierarchyNotice },
            });
            return result;
        }
        catch (CallHierarchyException exception)
        {
            var response = Error(id, exception.Changed ? -32801 : -32803,
                "Indexed call hierarchy unavailable: " + exception.Message
                + ". Save documents, refresh the index and prepare the hierarchy again; "
                + "reconnect if an open buffer was evicted. Use CLI/MCP for bounded graph diagnostics.");
            response["error"]!["data"] = new JsonObject { ["reason"] = exception.Message };
            return response;
        }
        catch (OperationCanceledException)
        {
            return requestCancellation.IsCancellationRequested
                ? Error(id, JsonRpcRequestCancelledCode, JsonRpcRequestCancelledMessage)
                : Error(id, -32803, "Indexed call hierarchy query deadline exceeded.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Error(id, -32803, "Indexed call hierarchy source unavailable. Save and refresh the index.");
        }
    }

    private JsonNode? PrepareCallHierarchy(JsonElement root, CallHierarchyRead read)
    {
        if (!TryResolveIndexedDocument(root, out var document))
            return null;
        if (!IsCallHierarchyLanguage(_reader.GetResourceFileMetadata(document.IndexedPath)?.Lang))
            return null;
        ReadCallHierarchyFile(document.IndexedPath, read);
        if (!TryExtractPositionToken(root, out var context, out var reason))
        {
            if (reason is FailureNoTokenAtPosition or FailureFileNotIndexed or FailureOutsideProject)
                return null;
            throw new CallHierarchyException("position_unavailable");
        }
        ReadCallHierarchyFile(context.IndexedPath, read);
        var definitions = _reader.GetDefinitions(context.Token, MaxReferencePositionCandidates + 1,
            exact: true, pathPatterns: [context.IndexedPath]);
        if (definitions.Count > MaxReferencePositionCandidates)
            throw new CallHierarchyException("symbol_candidate_budget_exceeded");
        var selected = FindDefinitionsAtPosition(definitions, context);
        if (selected.Count == 0)
        {
            var resolution = _reader.GetReferencePositionResolution(context.IndexedPath, context.Token,
                context.Line + 1, context.StartCharacter + 1, MaxReferencePositionCandidates);
            if (!resolution.IdentityAvailable || resolution.CandidatesTruncated)
                throw new CallHierarchyException("reference_identity_unavailable");
            selected = ResolveReferenceTargetsAtPosition(context);
            if (selected.Count == 0)
                throw new CallHierarchyException("unresolved_or_ambiguous_position");
        }
        if (selected.Count != 1)
            throw new CallHierarchyException("ambiguous_position");
        if (!IsCallHierarchyCallable(selected[0]))
            return null;
        return new JsonArray(CreateCallHierarchyItem(selected[0], read));
    }

    private static bool IsCallHierarchyCallable(SymbolResult symbol) =>
        symbol.SymbolId.HasValue
        && symbol.Kind is "function" or "test.method" or "lambda"
        && IsCallHierarchyLanguage(symbol.Lang);

    private static bool IsCallHierarchyLanguage(string? language) =>
        language is "csharp" or "java" or "javascript" or "typescript" or "python"
            or "go" or "rust" or "c" or "cpp" or "kotlin" or "ruby" or "php" or "swift";

    private JsonArray ExpandCallHierarchy(JsonElement root, bool incoming, CallHierarchyRead read)
    {
        if (!TryGet(root, out var data, "params", "item", "data") || data.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Call hierarchy item data is required.");
        var token = data.GetString()!;
        if (token.Length > MaxCallHierarchyDataChars)
            throw new ArgumentException("Call hierarchy item data is too long.");
        var prefix = CallHierarchyDataPrefix(read);
        if (!token.StartsWith(prefix, StringComparison.Ordinal))
            throw new CallHierarchyException("stale_item", changed: true);
        var resolution = _reader.ResolveGraphSymbolSelector(token[prefix.Length..]);
        if (resolution.Status != GraphSymbolSelectorStatus.Success || resolution.Definition is not { } definition)
            throw new CallHierarchyException("stale_item", changed: true);
        if (!IsCallHierarchyCallable(definition))
            throw new ArgumentException("Call hierarchy item is not callable.");
        var canonical = CreateCallHierarchyItem(definition, read);
        if (!TryGet(root, out var uri, "params", "item", "uri") || uri.ValueKind != JsonValueKind.String
            || uri.GetString() != canonical["uri"]!.GetValue<string>())
            throw new ArgumentException("Call hierarchy item URI does not match its identity.");

        var sites = _reader.GetCallHierarchySites(definition.SymbolId!.Value, incoming, MaxCallHierarchySites + 1);
        if (sites.Count > MaxCallHierarchySites)
            throw new CallHierarchyException("call_site_budget_exceeded");
        var groups = new Dictionary<long, (JsonObject Item, JsonArray Ranges)>();
        var definitionsById = new Dictionary<long, DefinitionResult> { [definition.SymbolId.Value] = definition };
        var seen = new HashSet<(long Source, long Target, int Line, int Column, int Length)>();
        foreach (var site in sites)
        {
            _reader.Cancellation.ThrowIfCancellationRequested();
            if (site.SourceId is not long sourceId || site.TargetId is not long targetId
                || site.ResolutionState != "resolved"
                || (incoming && targetId != definition.SymbolId))
                throw new CallHierarchyException("unresolved_or_ambiguous_calls");
            var source = GetDefinition(sourceId);
            var target = GetDefinition(targetId);
            if (!IsCallHierarchyCallable(source) || !IsCallHierarchyCallable(target))
                throw new CallHierarchyException("call_endpoint_unavailable");
            var otherId = incoming ? sourceId : targetId;
            if (!groups.TryGetValue(otherId, out var group))
            {
                if (groups.Count >= MaxCallHierarchyItems)
                    throw new CallHierarchyException("call_item_budget_exceeded");
                group = (CreateCallHierarchyItem(incoming ? source : target, read), new JsonArray());
                groups.Add(otherId, group);
            }
            var sourceItem = incoming ? group.Item : canonical;
            var lines = ReadCallHierarchyFile(site.Path, read);
            if (site.Path != source.Path || site.Line <= 0 || site.Line > lines.Count
                || lines[site.Line - 1] is not { } line || site.Column <= 0)
                throw new CallHierarchyException("call_site_range_unavailable");
            var start = site.Column - 1;
            var span = ExtractTokenAtUtf16Position(line, start);
            var (tokenStart, tokenEnd) = FindTokenRangeAtUtf16Position(line, start);
            if (span == null || tokenStart != start || tokenEnd <= start
                || !string.Equals(span.TrimStart('@'), site.Name.TrimStart('@'), StringComparison.Ordinal))
                throw new CallHierarchyException("call_site_range_unavailable");
            var range = ToRange(site.Line, site.Column, site.Line, tokenEnd + 1);
            var sourceRange = sourceItem["range"]!;
            if (ComparePosition(site.Line - 1, start,
                    sourceRange["start"]!["line"]!.GetValue<int>(), sourceRange["start"]!["character"]!.GetValue<int>()) < 0
                || ComparePosition(site.Line - 1, tokenEnd,
                    sourceRange["end"]!["line"]!.GetValue<int>(), sourceRange["end"]!["character"]!.GetValue<int>()) > 0)
                throw new CallHierarchyException("call_site_outside_callable");
            if (seen.Add((sourceId, targetId, site.Line, site.Column, tokenEnd - start)))
                group.Ranges.Add(range);
        }
        var result = new JsonArray();
        foreach (var group in groups.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            result.Add(new JsonObject { [incoming ? "from" : "to"] = group.Item, ["fromRanges"] = group.Ranges });
        return result;

        DefinitionResult GetDefinition(long symbolId)
        {
            if (definitionsById.TryGetValue(symbolId, out var cached))
                return cached;
            var found = _reader.GetDefinitionBySelector(new SymbolSelector(symbolId))
                ?? throw new CallHierarchyException("call_endpoint_unavailable");
            definitionsById.Add(symbolId, found);
            return found;
        }
    }

    private string CallHierarchyDataPrefix(CallHierarchyRead read) =>
        _callHierarchySession + ":" + SymbolSelector.BuildGenerationFingerprint(read.Generation) + ":";

    private JsonObject CreateCallHierarchyItem(DefinitionResult symbol, CallHierarchyRead read)
    {
        var lines = ReadCallHierarchyFile(symbol.Path, read);
        var fullPath = read.Files[symbol.Path].FullPath;
        var identifierLineNumber = symbol.Line > 0 ? symbol.Line : Math.Max(1, symbol.StartLine);
        var cache = new Dictionary<int, string?>
        {
            [identifierLineNumber - 1] = identifierLineNumber <= lines.Count ? lines[identifierLineNumber - 1] : null,
        };
        var identifier = GetSymbolIdentifierPosition(symbol, fullPath, cache);
        var startLine = symbol.StartLine;
        var endLine = symbol.EndLine;
        if (startLine <= 0 || endLine < startLine || endLine > lines.Count
            || identifier.Line < startLine || identifier.Line > endLine
            || lines[identifier.Line - 1] is not { } identifierLine
            || identifier.StartColumn < 1 || identifier.StartColumn > identifierLine.Length
            || lines[endLine - 1] is not { } lastLine)
            throw new CallHierarchyException("symbol_range_unavailable");
        var (tokenStart, tokenEnd) = FindTokenRangeAtUtf16Position(identifierLine, identifier.StartColumn - 1);
        var actualName = ExtractTokenAtUtf16Position(identifierLine, identifier.StartColumn - 1);
        if (tokenStart != identifier.StartColumn - 1 || tokenEnd <= tokenStart
            || !string.Equals(actualName?.TrimStart('@'), symbol.Name.TrimStart('@'), StringComparison.Ordinal))
            throw new CallHierarchyException("symbol_range_unavailable");
        return new JsonObject
        {
            ["name"] = symbol.Name,
            ["kind"] = SymbolKind(symbol),
            ["uri"] = PathToUri(fullPath),
            ["range"] = ToRange(startLine, 1, endLine, lastLine.Length + 1),
            ["selectionRange"] = ToRange(identifier.Line, tokenStart + 1, identifier.Line, tokenEnd + 1),
            ["detail"] = "Indexed call evidence · " + TruncateDocumentSymbolDetail(symbol.Signature),
            ["data"] = CallHierarchyDataPrefix(read) + _reader.BuildSymbolCandidateSelector(symbol).Selector,
        };
    }

    private IReadOnlyList<string?> ReadCallHierarchyFile(string indexedPath, CallHierarchyRead read)
    {
        if (read.Files.TryGetValue(indexedPath, out var cached))
            return cached.Lines;
        _reader.Cancellation.ThrowIfCancellationRequested();
        var workspaceRoot = _projectRoot ?? (_workspaceFolders.Count == 1 ? _workspaceFolders[0] : null);
        if ((!Path.IsPathRooted(indexedPath) && workspaceRoot == null)
            || !TryResolveIndexedFilePath(indexedPath, workspaceRoot, out var fullPath)
            || !TryResolveDocumentPath(fullPath, out _, out _, out _))
            throw new CallHierarchyException("source_path_unavailable");
        var metadata = _reader.GetResourceFileMetadata(indexedPath);
        if (metadata?.Checksum == null)
            throw new CallHierarchyException("source_checksum_unavailable");
        var remainingBytes = Math.Min(MaxPositionDocumentBytes, MaxLiveDocumentBytes - read.SourceBytes);
        if (remainingBytes <= 0)
            throw new CallHierarchyException("source_budget_exceeded");
        LoadedFileContent loaded;
        try
        {
            loaded = new FileContentLoader(remainingBytes).Load(fullPath, indexedPath, indexedPath, _reader.Cancellation);
        }
        catch (FileIndexer.FileTooLargeSkippedException)
        {
            throw new CallHierarchyException("source_budget_exceeded");
        }
        catch (FileIndexer.BinaryFileSkippedException)
        {
            throw new CallHierarchyException("source_content_unavailable");
        }
        read.SourceBytes += loaded.RawBytes.LongLength;
        if (!string.Equals(loaded.Checksum, metadata.Checksum, StringComparison.OrdinalIgnoreCase))
            throw new CallHierarchyException("document_not_indexed", changed: true);
        using var content = new MemoryStream(loaded.RawBytes, writable: false);
        using var textReader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = textReader.ReadToEnd();
        var normalizedText = text.ReplaceLineEndings("\n");
        if (loaded.Content != normalizedText && loaded.Content != "\uFEFF" + normalizedText)
            throw new CallHierarchyException("source_content_unavailable");
        if (_liveDocumentStore.TryGetText(fullPath, out var live)
            && !string.Equals(live.ReplaceLineEndings("\n"), normalizedText, StringComparison.Ordinal))
            throw new CallHierarchyException("unsaved_document", changed: true);
        var lines = SplitPositionLines(text);
        read.Files.Add(indexedPath, (fullPath, FileIndexer.ComputeChecksum(loaded.RawBytes), lines));
        return lines;
    }
}
