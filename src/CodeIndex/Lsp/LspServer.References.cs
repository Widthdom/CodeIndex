using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeIndex.Lsp;

internal sealed partial class LspServer
{
    internal const int ReferencePageSize = 100;
    internal const int MaxReferenceRows = 10_000;
    internal const int MaxReferenceResponseBytes = 512 * 1024;
    internal const int MaxReferenceDeliveryBytes = 8 * 1024 * 1024;
    internal int ReferenceRowsForTesting { get; set; } = MaxReferenceRows;
    internal int ReferenceResponseBytesForTesting { get; set; } = MaxReferenceResponseBytes;
    internal int ReferenceDeliveryBytesForTesting { get; set; } = MaxReferenceDeliveryBytes;
    internal int ReferenceChunkBytesForTesting { get; set; } = MaxSymbolProgressChunkBytes;
    internal TimeSpan ReferenceTimeoutForTesting { get; set; } = TimeSpan.FromSeconds(5);
    internal Action? AfterReferenceChunkForTesting { get; set; }
    internal Action? BeforeReferenceValidationForTesting { get; set; }
    internal Action<CancellationToken>? BeforeReferenceReadForTesting { get; set; }

    private sealed class ReferenceDeliveryException(string reason) : Exception(reason);

    private JsonObject HandleReferences(JsonNode? id, JsonElement root, Action<JsonObject>? outbound,
        CancellationToken requestCancellation)
    {
        var partialToken = GetProgressToken(root, "partialResultToken");
        var workToken = GetProgressToken(root, "workDoneToken");
        // Direct in-process callers have no notification transport and use the no-token path.
        if (outbound == null)
            partialToken = workToken = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation, _reader.Cancellation);
        deadline.CancelAfter(ReferenceTimeoutForTesting);
        using var cancellationScope = _reader.BeginCancellationScope(deadline.Token);
        var delivered = 0;
        string? recoveryKind = null;
        var summary = "Reference request failed; discard any partial locations.";
        if (workToken != null)
            outbound!(CreateProgressNotification(workToken, CreateWorkDoneBegin("CodeIndex references")));
        try
        {
            var response = _reader.RunWithCancellationInterrupt(() =>
            {
                deadline.Token.ThrowIfCancellationRequested();
                BeforeReferenceReadForTesting?.Invoke(deadline.Token);
                var generation = _reader.GetCallHierarchyGeneration();
                var items = new JsonArray();
                var finalBaseBytes = MeasureJsonUtf8Bytes(Result(id?.DeepClone(), new JsonArray()));
                var chunkBaseBytes = partialToken == null ? 0
                    : MeasureJsonUtf8Bytes(CreateProgressNotification(partialToken, new JsonArray()));
                var currentBytes = partialToken == null ? finalBaseBytes : chunkBaseBytes;
                if (partialToken == null && currentBytes > ReferenceResponseBytesForTesting)
                    throw new ReferenceDeliveryException("response_byte_limit");
                long deliveryBytes = 0;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var examined = 0;
                if (TryExtractPositionToken(root, out var context, out var failureReason))
                {
                    var sources = ResolveLspReferenceSources(context);
                    recoveryKind = sources.Any(source => source.Definition == null && source.IndexedPath != null)
                        ? "file_name_fallback" : "selected_definition";
                    if (GetBool(root, "params", "context", "includeDeclaration") == true)
                    {
                        foreach (var definition in ResolveLspDefinitions(context))
                            Add(ToSymbolLocation(definition, context));
                    }
                    foreach (var source in sources)
                    {
                        var offset = 0;
                        while (true)
                        {
                            ValidateGeneration();
                            var pageSize = Math.Min(ReferencePageSize, ReferenceRowsForTesting - examined + 1);
                            var page = ReadLspReferencePage(source, pageSize, offset);
                            ValidateGeneration();
                            foreach (var reference in page)
                            {
                                deadline.Token.ThrowIfCancellationRequested();
                                if (++examined > ReferenceRowsForTesting)
                                    throw new ReferenceDeliveryException("reference_row_limit");
                                var column = Math.Max(reference.Column, 1);
                                Add(ToLocation(reference.Path, reference.Line, column, reference.Line,
                                    column + Math.Max(context.Token.Length, 1),
                                    GetLocationWorkspaceRoot(reference.Path, context)));
                            }
                            offset += page.Count;
                            if (page.Count < pageSize)
                                break;
                        }
                    }
                }
                else
                    RecordLookupFailure("textDocument/references", failureReason);
                BeforeReferenceValidationForTesting?.Invoke();
                ValidateGeneration();
                if (partialToken != null)
                    Flush();
                ValidateGeneration();
                summary = $"Returned {seen.Count} indexed reference locations.";
                return Result(id?.DeepClone(), partialToken == null ? items : null);

                void ValidateGeneration()
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (generation != _reader.GetCallHierarchyGeneration()
                        || (_ownedQueryDb != null && !_ownedQueryDb.IsQueryOnlySnapshotCurrent()))
                        throw new ReferenceDeliveryException("index_generation_changed");
                }

                void Add(JsonObject item)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    // Serialized Location identity includes the exact URI and all UTF-16 coordinates.
                    var key = item.ToJsonString(_jsonOptions);
                    if (!seen.Add(key))
                        return;
                    var itemBytes = MeasureJsonUtf8Bytes(item);
                    deliveryBytes += itemBytes;
                    if (seen.Count > ReferenceRowsForTesting || deliveryBytes > ReferenceDeliveryBytesForTesting)
                        throw new ReferenceDeliveryException("reference_delivery_limit");
                    if (partialToken != null && items.Count > 0
                        && (items.Count == MaxSymbolProgressChunkItems
                            || ProjectedBytes() > ReferenceChunkBytesForTesting))
                        Flush();
                    var nextBytes = ProjectedBytes();
                    if (nextBytes > (partialToken == null ? ReferenceResponseBytesForTesting : ReferenceChunkBytesForTesting))
                        throw new ReferenceDeliveryException(partialToken == null ? "response_byte_limit" : "progress_byte_limit");
                    items.Add(item);
                    currentBytes = nextBytes;

                    int ProjectedBytes()
                    {
                        if (!_jsonOptions.WriteIndented)
                            return currentBytes + itemBytes + (items.Count == 0 ? 0 : 1);
                        var candidate = (JsonArray)items.DeepClone();
                        candidate.Add(item.DeepClone());
                        return MeasureJsonUtf8Bytes(partialToken == null
                            ? Result(id?.DeepClone(), candidate)
                            : CreateProgressNotification(partialToken, candidate));
                    }
                }

                void Flush()
                {
                    if (items.Count == 0)
                        return;
                    ValidateGeneration();
                    var count = items.Count;
                    outbound!(CreateProgressNotification(partialToken!, items));
                    delivered += count;
                    items = [];
                    currentBytes = chunkBaseBytes;
                    AfterReferenceChunkForTesting?.Invoke();
                    deadline.Token.ThrowIfCancellationRequested();
                    if (workToken != null)
                        outbound!(CreateProgressNotification(workToken, new JsonObject
                        {
                            ["kind"] = "report", ["message"] = $"Streamed {delivered} reference locations.",
                        }));
                }
            });
            return response;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            summary = "Reference request cancelled; discard any partial locations.";
            return requestCancellation.IsCancellationRequested
                ? Error(id, JsonRpcRequestCancelledCode, JsonRpcRequestCancelledMessage)
                : Failure("query_deadline_exceeded");
        }
        catch (ReferenceDeliveryException ex)
        {
            return Failure(ex.Message);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return Error(id, JsonRpcInternalErrorCode, JsonRpcInternalErrorMessage);
        }
        finally
        {
            if (workToken != null)
                outbound!(CreateProgressNotification(workToken, CreateWorkDoneEnd(summary)));
        }

        JsonObject Failure(string reason)
        {
            summary = $"Reference request incomplete ({reason}); discard any partial locations.";
            var error = Error(id, reason == "index_generation_changed" ? -32801 : -32803, summary);
            error["error"]!["data"] = new JsonObject
            {
                ["reason"] = reason,
                ["deliveredLocationCount"] = delivered,
                ["recoveryKind"] = recoveryKind ?? "position_not_resolved",
                ["recovery"] = reason switch
                {
                    "response_byte_limit" => "Retry this reference request with a partialResultToken.",
                    "index_generation_changed" => "Wait for indexing to finish, then restart the reference request.",
                    _ when recoveryKind == "file_name_fallback" => "Use CLI references <symbol> --exact-name --format compact --limit 50 --path <indexed-file> --db <db>; follow metadata.next_cursor with --cursor and retain only results whose file exactly equals the original indexed path (case-sensitive). The path option is only a prefilter; no definition selector is required. See docs/lsp-references.md for path and pagination limits.",
                    _ => "Use CLI inspect <symbol> --exact-name --json --db <db>; select the matching definition and follow its references graph-section cursor. See the LSP references documentation for identity-preserving pagination.",
                },
            };
            return error;
        }
    }
}
