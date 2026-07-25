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
    private const int DefaultLimit = 50;
    internal const int MaxWorkspaceSymbols = 1000;
    private const int MaxReferencePositionCandidates = 256;
    private const int MaxWorkspaceFolders = 32;
    internal const int MaxLspFrameBytes = LspProtocol.MaxFrameBytes;
    internal const int MaxLspResponseFrameBytes = LspProtocol.MaxResponseFrameBytes;
    internal const int MaxLspHeaderLineBytes = LspProtocol.MaxHeaderLineBytes;
    internal const int MaxLspHeaderCount = LspProtocol.MaxHeaderCount;
    internal const int MaxLspHeaderBytes = LspProtocol.MaxHeaderBytes;
    internal const int MaxPositionDocumentBytes = 4 * 1024 * 1024;
    internal const int MaxLiveDocumentBytes = 16 * 1024 * 1024;
    internal const int MaxPooledPayloadBufferBytes = LspProtocol.MaxPooledPayloadBufferBytes;
    internal const int MaxLiveDocuments = 64;
    internal const int MaxContentChangesPerNotification = 64;
    internal const int MaxTextDocumentUriChars = McpBoundedText.MaxResourceUriChars;
    internal const int MaxLspRequestIdRawBytes = LspProtocol.MaxRequestIdRawBytes;
    internal const int MaxJsonDepth = LspProtocol.MaxJsonDepth;
    internal const int MaxRequestIdStringChars = LspProtocol.MaxRequestIdStringChars;
    internal const int MaxDocumentSymbols = 1000;
    internal const int MaxDocumentSymbolMaterialization = MaxDocumentSymbols;
    internal const int MaxDocumentSymbolDetailChars = 512;
    internal const int MaxDocumentSymbolResponseBytes = 512 * 1024;
    internal static int? DocumentSymbolResponseBytesForTesting { get; set; }
    private static readonly AsyncLocal<Action<string>?> ScopedPositionFileLengthCheckedForTesting = new();
    internal static Action<string>? PositionFileLengthCheckedForTesting
    {
        get => ScopedPositionFileLengthCheckedForTesting.Value;
        set => ScopedPositionFileLengthCheckedForTesting.Value = value;
    }
    internal const int MaxSymbolProgressChunkItems = 100;
    internal const int MaxSymbolProgressChunkBytes = 64 * 1024;
    private const int MaxPendingLspMessages = 16;
    private const int MaxPendingSymbolNotifications = 2;
    private const int MaxPendingOverloadResponses = 16;
    internal const int MaxPositionLineChars = 16 * 1024;
    internal const int MaxCompletionItems = 100;
    internal const int MaxInlayHintItems = 200;
    internal const int MaxSemanticTokenItems = 1000;
    internal const int MaxDocumentPathFallbackCandidates = 32;
    internal const int MaxUnknownMethodDiagnosticChars = 240;
    private const int JsonRpcInvalidParamsCode = -32602;
    private const int JsonRpcInternalErrorCode = -32603;
    private const int JsonRpcRequestCancelledCode = -32800;
    private const int JsonRpcServerBusyCode = -32000;
    private const string JsonRpcInvalidParamsMessage = "Invalid params";
    private const string JsonRpcInternalErrorMessage = "Internal error";
    private const string JsonRpcRequestCancelledMessage = "Request cancelled";
    private const string JsonRpcServerBusyMessage = "Server busy";
    private const string LspLookupFailureEventName = "lsp.lookup_failed";
    private const string LspLookupFailureReasonTag = "lsp.lookup.failure_reason";
    private const string LspMethodTag = "lsp.method";
    private const string FailureInvalidPosition = "invalid_position";
    private const string FailureOutsideProject = "outside_project";
    private const string FailureDocumentPathUnresolved = "document_path_unresolved";
    private const string FailureFileNotIndexed = "file_not_indexed";
    private const string FailureIndexedFileUnresolved = "indexed_file_unresolved";
    private const string FailurePathCasingMismatch = "path_casing_mismatch";
    private const string FailurePositionFileTooLarge = "position_file_too_large";
    private const string FailurePositionLineTooLong = "position_line_too_long";
    private const string FailurePositionLineMissing = "position_line_missing";
    private const string FailurePositionFileUnreadable = "position_file_unreadable";
    private const string FailureNoTokenAtPosition = "no_token_at_position";
    internal const string ReadDiagnosticEndOfStream = LspProtocol.ReadDiagnosticEndOfStream;
    internal const string ReadDiagnosticIncompleteHeader = LspProtocol.ReadDiagnosticIncompleteHeader;
    internal const string ReadDiagnosticHeaderLineTooLarge = LspProtocol.ReadDiagnosticHeaderLineTooLarge;
    internal const string ReadDiagnosticHeaderSectionTooLarge = LspProtocol.ReadDiagnosticHeaderSectionTooLarge;
    internal const string ReadDiagnosticDuplicateContentLength = LspProtocol.ReadDiagnosticDuplicateContentLength;
    internal const string ReadDiagnosticMalformedContentLength = LspProtocol.ReadDiagnosticMalformedContentLength;
    internal const string ReadDiagnosticNegativeContentLength = LspProtocol.ReadDiagnosticNegativeContentLength;
    internal const string ReadDiagnosticContentLengthTooLarge = LspProtocol.ReadDiagnosticContentLengthTooLarge;
    internal const string ReadDiagnosticMissingContentLength = LspProtocol.ReadDiagnosticMissingContentLength;
    internal const string ReadDiagnosticIncompleteBody = LspProtocol.ReadDiagnosticIncompleteBody;
    private static readonly string[] SemanticTokenTypes =
    [
        "namespace",
        "type",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "enumMember",
        "event",
        "function",
        "method",
        "macro",
        "keyword",
        "modifier",
        "comment",
        "string",
        "number",
        "regexp",
        "operator",
        "decorator",
        "field",
    ];
    private static readonly string[] SemanticTokenModifiers =
    [
        "declaration",
        "definition",
        "readonly",
        "static",
        "deprecated",
        "abstract",
        "async",
        "modification",
        "documentation",
        "defaultLibrary",
    ];
    private DbReader _reader;
    private DbContext? _ownedQueryDb;
    private readonly string? _ownedQueryDbPath;
    private readonly string _version;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string? _projectRoot;
    private readonly StringComparison _pathStringComparison;
    private bool _shutdownRequested;
    private bool _exitRequested;
    private bool _exitRequestedBeforeShutdown;
    private readonly List<string> _workspaceFolders = [];
    private readonly LspLiveDocumentStore _liveDocumentStore;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requestCancellations = new(StringComparer.Ordinal);
    private long _contentChangeEntriesDropped;
    internal Action<CancellationToken>? BeforeSymbolRequestForTesting { get; set; }

    private readonly record struct PositionTokenContext(string Token, string ResolvedPath, string IndexedPath, string? WorkspaceRoot, int Line, int StartCharacter, int EndCharacter);
    private readonly record struct DocumentSymbolNode(SymbolResult Symbol, JsonObject Item);
    private readonly record struct IndexedDocumentContext(string DocumentPath, string ResolvedPath, string IndexedPath, string? WorkspaceRoot);
    private readonly record struct InboundMessage(string Payload, string? RequestKey, CancellationTokenSource? RequestCancellation);
    private readonly record struct SymbolResponse(
        JsonArray FinalItems,
        IEnumerable<JsonNode> PartialItems,
        int ReturnedCount,
        bool Truncated);
    private readonly record struct DocumentSymbolTreeResult(JsonArray Roots, int RemovedCount);
    private readonly record struct PartialResultEmission(int EmittedCount, bool Truncated, bool Cancelled);
    internal readonly record struct MessageReadResult(bool Success, string Payload);
    internal readonly record struct LspMessageReadDiagnostic(
        string Code,
        string Message,
        int? ContentLength = null,
        int? MaxContentLength = null);

    public LspServer(DbReader reader, string version, JsonSerializerOptions jsonOptions, string? projectRoot = null)
    {
        _reader = reader;
        _version = version;
        _jsonOptions = jsonOptions;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : projectRoot;
        _pathStringComparison = PathCasing.ComparisonFor(_projectRoot ?? Environment.CurrentDirectory);
        var liveDocumentComparer = _pathStringComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _liveDocumentStore = new LspLiveDocumentStore(
            liveDocumentComparer,
            _pathStringComparison,
            MaxLiveDocuments,
            MaxPositionDocumentBytes,
            MaxLiveDocumentBytes);
        if (_projectRoot != null)
            _workspaceFolders.Add(Path.GetFullPath(_projectRoot));
    }

    internal LspServer(
        DbContext queryDb,
        string queryDbPath,
        string version,
        JsonSerializerOptions jsonOptions,
        string? projectRoot = null)
        : this(new DbReader(queryDb), version, jsonOptions, projectRoot)
    {
        ArgumentNullException.ThrowIfNull(queryDb);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryDbPath);
        if (queryDb.OpenIntent != DbOpenIntent.QueryOnly)
            throw new ArgumentException("LSP-owned database context must use QueryOnly intent.", nameof(queryDb));

        _ownedQueryDb = queryDb;
        _ownedQueryDbPath = queryDbPath;
    }

    internal long LiveDocumentBytesForTests => _liveDocumentStore.Bytes;

    internal long LiveDocumentEvictionCountForTests => _liveDocumentStore.EvictionCount;

    internal long LiveDocumentEvictedBytesForTests => _liveDocumentStore.EvictedBytes;

    internal long ContentChangeEntriesDroppedForTests => _contentChangeEntriesDropped;

    /// <summary>
    /// Compatibility wrapper that runs without caller cancellation. Prefer <see cref="RunAsync"/>
    /// when the caller has a shutdown or disconnect token.
    /// caller cancellation を持たない互換 wrapper。shutdown / disconnect token がある場合は
    /// <see cref="RunAsync"/> を使う。
    /// </summary>
    public int Run(Stream input, Stream output) => RunAsync(input, output, CancellationToken.None).GetAwaiter().GetResult();

    public int Run(Stream input, Stream output, CancellationToken cancellationToken)
        => RunAsync(input, output, cancellationToken).GetAwaiter().GetResult();

    public async Task<int> RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var messages = Channel.CreateBounded<InboundMessage>(new BoundedChannelOptions(MaxPendingLspMessages)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var overloadResponses = Channel.CreateBounded<JsonObject>(
            new BoundedChannelOptions(MaxPendingOverloadResponses)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
        using var exitCancellation = new CancellationTokenSource();
        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            exitCancellation.Token);
        using var outputGate = new SemaphoreSlim(1, 1);
        var processor = ProcessInboundMessagesAsync(
            messages.Reader,
            output,
            outputGate,
            exitCancellation,
            cancellationToken);
        var overloadResponseDrain = DrainServerResponsesAsync(
            overloadResponses.Reader,
            output,
            outputGate,
            exitCancellation,
            cancellationToken);

        try
        {
            while (true)
            {
                var read = await TryReadMessageAsync(input, readCancellation.Token).ConfigureAwait(false);
                if (!read.Success)
                    break;

                cancellationToken.ThrowIfCancellationRequested();
                if (TryHandleCancellationNotification(read.Payload))
                    continue;

                var inbound = CreateInboundMessage(read.Payload, cancellationToken);
                if (messages.Writer.TryWrite(inbound))
                    continue;

                var busyResponse = CreateServerBusyResponse(read.Payload);
                if (busyResponse != null)
                {
                    ReleaseInboundMessage(inbound);
                    await overloadResponses.Writer
                        .WriteAsync(busyResponse, readCancellation.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await messages.Writer
                        .WriteAsync(inbound, readCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    ReleaseInboundMessage(inbound);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (exitCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The exit notification stops a pending frame read. / exit notification で pending frame read を停止する。
        }
        finally
        {
            messages.Writer.TryComplete();
            overloadResponses.Writer.TryComplete();
            try
            {
                await Task.WhenAll(processor, overloadResponseDrain).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Preserve the public Run/RunAsync cancellation contract below.
                // 下の public Run/RunAsync cancellation contract を維持する。
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return _exitRequestedBeforeShutdown ? CommandExitCodes.UsageError : CommandExitCodes.Success;
    }

    internal JsonObject? HandleMessage(string payload) =>
        HandleMessage(payload, outbound: null, CancellationToken.None);

    private JsonObject? HandleMessage(
        string payload,
        Action<JsonObject>? outbound,
        CancellationToken requestCancellation)
    {
        // Run() normally obtains payloads through TryReadMessage, but internal callers can bypass
        // that frame reader; keep the JSON parse under the same byte budget either way.
        if (payload.Length > MaxLspFrameBytes || Encoding.UTF8.GetByteCount(payload) > MaxLspFrameBytes)
            return Error(null, -32700, "Parse error");

        JsonDocument document;
        try
        {
            document = BoundedJson.ParseDocument(payload, MaxLspFrameBytes, MaxJsonDepth);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return Error(null, -32700, LspProtocol.FormatParseErrorMessage(payload));
        }

        using (document)
        {
            JsonNode? id = null;
            var hasId = false;

            try
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Error(null, -32600, "Invalid Request");

                var method = root.TryGetProperty("method", out var methodElement) ? methodElement.GetString() : null;
                hasId = root.TryGetProperty("id", out var idElement);
                if (hasId && !LspProtocol.TryParseRequestId(payload, idElement, out id, out var requestIdError))
                    return Error(null, -32600, requestIdError);

                if (method == null)
                    return hasId ? Error(id, -32600, "Invalid Request") : null;

                RefreshOwnedQuerySnapshot();
                using var activity = StartLspRequestActivity(method);
                return method switch
                {
                    "initialize" => HandleInitialize(id, root),
                    "initialized" => null,
                    "shutdown" => HandleShutdown(id),
                    "exit" => HandleExit(),
                    "workspace/didChangeWorkspaceFolders" => HandleDidChangeWorkspaceFolders(root),
                    "textDocument/didOpen" => HandleDidOpenTextDocument(root),
                    "textDocument/didChange" => HandleDidChangeTextDocument(root),
                    "textDocument/didClose" => HandleDidCloseTextDocument(root),
                    "$/cancelRequest" => null,
                    "workspace/symbol" => HandleSymbolRequest(
                        id,
                        root,
                        documentSymbols: false,
                        outbound,
                        requestCancellation),
                    "textDocument/documentSymbol" => HandleSymbolRequest(
                        id,
                        root,
                        documentSymbols: true,
                        outbound,
                        requestCancellation),
                    "textDocument/definition" => Result(id, Definition(root, "textDocument/definition")),
                    "textDocument/declaration" => Result(id, Definition(root, "textDocument/declaration")),
                    "textDocument/references" => Result(id, References(root, "textDocument/references")),
                    "textDocument/hover" => Result(id, Hover(root, "textDocument/hover")),
                    "textDocument/completion" => Result(id, Completion(root, "textDocument/completion")),
                    "textDocument/documentHighlight" => Result(id, DocumentHighlight(root, "textDocument/documentHighlight")),
                    "textDocument/semanticTokens/full" => Result(id, SemanticTokensFull(root)),
                    "textDocument/inlayHint" => Result(id, InlayHint(root)),
                    _ => hasId ? Error(id, -32601, $"Method not found: {SanitizeUnknownMethod(method)}") : null,
                };
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException)
            {
                return hasId ? Error(id, JsonRpcInvalidParamsCode, JsonRpcInvalidParamsMessage) : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return hasId ? Error(id, JsonRpcInternalErrorCode, JsonRpcInternalErrorMessage) : null;
            }
        }
    }

    private async Task ProcessInboundMessagesAsync(
        ChannelReader<InboundMessage> reader,
        Stream output,
        SemaphoreSlim outputGate,
        CancellationTokenSource exitCancellation,
        CancellationToken serverCancellation)
    {
        try
        {
            await foreach (var inbound in reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    var notifications = Channel.CreateBounded<JsonObject>(
                        new BoundedChannelOptions(MaxPendingSymbolNotifications)
                        {
                            SingleReader = true,
                            SingleWriter = true,
                            FullMode = BoundedChannelFullMode.Wait,
                        });
                    using var notificationCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
                    using var processingCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            inbound.RequestCancellation?.Token ?? serverCancellation,
                            notificationCancellation.Token);
                    var notificationDrain = DrainServerNotificationsAsync(
                        notifications.Reader,
                        output,
                        outputGate,
                        notificationCancellation);
                    JsonObject? response;
                    try
                    {
                        response = HandleMessage(
                            inbound.Payload,
                            notification => notifications.Writer
                                .WriteAsync(notification, notificationCancellation.Token)
                                .AsTask()
                                .GetAwaiter()
                                .GetResult(),
                            processingCancellation.Token);
                    }
                    finally
                    {
                        notifications.Writer.TryComplete();
                        await notificationDrain.ConfigureAwait(false);
                    }

                    if (response != null)
                    {
                        await WriteResponseMessageAsync(
                            output,
                            outputGate,
                            response,
                            serverCancellation).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ReleaseInboundMessage(inbound);
                }

                if (_exitRequested)
                {
                    exitCancellation.Cancel();
                    break;
                }
            }
        }
        catch
        {
            exitCancellation.Cancel();
            throw;
        }
        finally
        {
            while (reader.TryRead(out var pending))
                ReleaseInboundMessage(pending);
        }
    }

    private async Task DrainServerNotificationsAsync(
        ChannelReader<JsonObject> notifications,
        Stream output,
        SemaphoreSlim outputGate,
        CancellationTokenSource cancellation)
    {
        try
        {
            await foreach (var notification in notifications
                .ReadAllAsync(cancellation.Token)
                .ConfigureAwait(false))
            {
                await WriteServerNotificationAsync(
                    output,
                    outputGate,
                    notification,
                    cancellation.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            cancellation.Cancel();
            throw;
        }
    }

    private async Task DrainServerResponsesAsync(
        ChannelReader<JsonObject> responses,
        Stream output,
        SemaphoreSlim outputGate,
        CancellationTokenSource exitCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var response in responses
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                await WriteResponseMessageAsync(
                    output,
                    outputGate,
                    response,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            exitCancellation.Cancel();
            throw;
        }
    }

    private InboundMessage CreateInboundMessage(string payload, CancellationToken serverCancellation)
    {
        if (!TryGetRequestKey(payload, out var requestKey))
            return new InboundMessage(payload, null, null);

        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        if (!_requestCancellations.TryAdd(requestKey, requestCancellation))
        {
            requestCancellation.Dispose();
            return new InboundMessage(payload, null, null);
        }

        return new InboundMessage(payload, requestKey, requestCancellation);
    }

    private void ReleaseInboundMessage(InboundMessage inbound)
    {
        if (inbound.RequestCancellation == null)
            return;

        if (inbound.RequestKey != null
            && _requestCancellations.TryGetValue(inbound.RequestKey, out var registered)
            && ReferenceEquals(registered, inbound.RequestCancellation))
        {
            _requestCancellations.TryRemove(inbound.RequestKey, out _);
        }

        inbound.RequestCancellation.Dispose();
    }

    private bool TryHandleCancellationNotification(string payload)
    {
        try
        {
            using var document = BoundedJson.ParseDocument(payload, MaxLspFrameBytes, MaxJsonDepth);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("method", out var method)
                || method.ValueKind != JsonValueKind.String
                || !string.Equals(method.GetString(), "$/cancelRequest", StringComparison.Ordinal))
            {
                return false;
            }

            if (TryGet(root, out var requestId, "params", "id")
                && TryGetRequestKey(requestId, out var requestKey)
                && _requestCancellations.TryGetValue(requestKey, out var requestCancellation))
            {
                try
                {
                    requestCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The request completed while cancellation was being dispatched.
                    // cancellation dispatch 中に request が完了した。
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static JsonObject? CreateServerBusyResponse(string payload)
    {
        try
        {
            using var document = BoundedJson.ParseDocument(payload, MaxLspFrameBytes, MaxJsonDepth);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("method", out var method)
                || method.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("id", out var requestId)
                || !LspProtocol.TryParseRequestId(payload, requestId, out var id, out _))
            {
                return null;
            }

            return Error(id, JsonRpcServerBusyCode, JsonRpcServerBusyMessage);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool TryGetRequestKey(string payload, out string requestKey)
    {
        requestKey = string.Empty;
        try
        {
            using var document = BoundedJson.ParseDocument(payload, MaxLspFrameBytes, MaxJsonDepth);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("method", out var method)
                && method.ValueKind == JsonValueKind.String
                && root.TryGetProperty("id", out var requestId)
                && TryGetRequestKey(requestId, out requestKey);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryGetRequestKey(JsonElement requestId, out string requestKey)
    {
        requestKey = string.Empty;
        if (requestId.ValueKind == JsonValueKind.String)
        {
            var value = requestId.GetString() ?? string.Empty;
            if (value.Length > MaxRequestIdStringChars)
                return false;
            requestKey = "s:" + value;
            return true;
        }

        if (requestId.ValueKind == JsonValueKind.Number && requestId.TryGetInt64(out var integer))
        {
            requestKey = "n:" + integer.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private void RefreshOwnedQuerySnapshot()
    {
        if (_ownedQueryDb == null
            || !_ownedQueryDb.QueryOnlySnapshotRequiresRefresh
            || _ownedQueryDb.IsQueryOnlySnapshotCurrent())
        {
            return;
        }

        var replacementDb = new DbContext(DbOpenIntent.QueryOnly, _ownedQueryDbPath!);
        DbReader? replacementReader = null;
        try
        {
            replacementReader = new DbReader(replacementDb);
        }
        catch
        {
            replacementDb.Dispose();
            throw;
        }

        _reader.Dispose();
        _ownedQueryDb.Dispose();
        _reader = replacementReader;
        _ownedQueryDb = replacementDb;
    }

    internal static bool ShouldRentPayloadBuffer(int byteCount)
    {
        return LspProtocol.ShouldRentPayloadBuffer(byteCount);
    }

    internal static void ClearSensitivePayloadBufferForTests(byte[] buffer, int usedBytes) =>
        LspProtocol.ClearSensitivePayloadBufferForTests(buffer, usedBytes);

    private static string SanitizeUnknownMethod(string method)
    {
        var wasTruncated = method.Length > MaxUnknownMethodDiagnosticChars;
        var boundedMethod = wasTruncated ? method[..MaxUnknownMethodDiagnosticChars] : method;
        var sanitized = boundedMethod
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
        return AppendEllipsisIfNeeded(sanitized, wasTruncated);
    }

    private static string AppendEllipsisIfNeeded(string value, bool wasTruncated)
        => wasTruncated && !value.EndsWith("...", StringComparison.Ordinal)
            ? value + "..."
            : value;

    private JsonObject HandleShutdown(JsonNode? id)
    {
        _shutdownRequested = true;
        return Result(id, null);
    }

    private JsonObject? HandleExit()
    {
        _exitRequestedBeforeShutdown = !_shutdownRequested;
        _exitRequested = true;
        return null;
    }

    private JsonObject HandleInitialize(JsonNode? id, JsonElement root)
    {
        CaptureInitializeWorkspaceFolders(root);
        return Result(id, BuildInitializeResult());
    }

    private JsonObject? HandleDidChangeWorkspaceFolders(JsonElement root)
    {
        if (TryGet(root, out var removed, "params", "event", "removed") && removed.ValueKind == JsonValueKind.Array)
        {
            foreach (var folder in removed.EnumerateArray())
            {
                if (TryGetWorkspaceFolderPath(folder, out var path))
                    _workspaceFolders.RemoveAll(existing => string.Equals(existing, path, _pathStringComparison));
            }
        }

        if (TryGet(root, out var added, "params", "event", "added") && added.ValueKind == JsonValueKind.Array)
        {
            foreach (var folder in added.EnumerateArray())
            {
                if (_workspaceFolders.Count >= MaxWorkspaceFolders)
                    break;
                if (TryGetWorkspaceFolderPath(folder, out var path)
                    && !_workspaceFolders.Any(existing => string.Equals(existing, path, _pathStringComparison)))
                {
                    _workspaceFolders.Add(path);
                }
            }
        }

        Activity.Current?.SetTag("lsp.workspace_folder_count", _workspaceFolders.Count);
        return null;
    }

    private JsonObject? HandleDidOpenTextDocument(JsonElement root)
    {
        var uri = GetTextDocumentUri(root);
        if (TryGet(root, out var textElement, "params", "textDocument", "text") && textElement.ValueKind == JsonValueKind.String)
            SetLiveDocumentText(uri, textElement.GetString() ?? string.Empty);
        return null;
    }

    private JsonObject? HandleDidChangeTextDocument(JsonElement root)
    {
        var uri = GetTextDocumentUri(root);
        if (!TryGet(root, out var changes, "params", "contentChanges") || changes.ValueKind != JsonValueKind.Array)
            return null;

        string? latestText = null;
        var changeCount = changes.GetArrayLength();
        var startIndex = 0;
        if (changeCount > MaxContentChangesPerNotification)
        {
            startIndex = changeCount - MaxContentChangesPerNotification;
            _contentChangeEntriesDropped += startIndex;
            Activity.Current?.SetTag("lsp.content_changes.dropped", _contentChangeEntriesDropped);
        }

        for (var i = startIndex; i < changeCount; i++)
        {
            var change = changes[i];
            if (change.ValueKind == JsonValueKind.Object
                && change.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                latestText = textElement.GetString() ?? string.Empty;
            }
        }

        if (latestText != null)
            SetLiveDocumentText(uri, latestText);
        return null;
    }

    private JsonObject? HandleDidCloseTextDocument(JsonElement root)
    {
        var uri = GetTextDocumentUri(root);
        if (TryGetLiveDocumentKeyFromUri(uri, out var key))
            _liveDocumentStore.Remove(key);
        return null;
    }

    private void SetLiveDocumentText(string uri, string text)
    {
        if (!TryGetLiveDocumentKeyFromUri(uri, out var key))
            return;

        _liveDocumentStore.SetText(key, text);
        Activity.Current?.SetTag("lsp.live_documents.bytes", _liveDocumentStore.Bytes);
        Activity.Current?.SetTag("lsp.live_documents.eviction_count", _liveDocumentStore.EvictionCount);
    }

    private bool TryGetLiveDocumentKeyFromUri(string uri, out string key)
    {
        key = string.Empty;
        try
        {
            key = Path.GetFullPath(UriToPath(uri));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static Activity? StartLspRequestActivity(string method)
    {
        var activity = CodeIndexTelemetry.ActivitySource.StartActivity("lsp.request", ActivityKind.Server);
        activity?.SetTag("rpc.system", "jsonrpc");
        activity?.SetTag("rpc.service", "lsp");
        activity?.SetTag("rpc.method", method);
        return activity;
    }

}
