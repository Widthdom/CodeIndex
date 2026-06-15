using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Mcp;
using CodeIndex.Models;

namespace CodeIndex.Lsp;

internal sealed class LspServer : IDisposable
{
    private const int DefaultLimit = 50;
    internal const int MaxWorkspaceSymbols = 1000;
    private const int MaxWorkspaceFolders = 32;
    internal const int MaxLspFrameBytes = 8 * 1024 * 1024;
    internal const int MaxLspHeaderLineBytes = 8 * 1024;
    internal const int MaxLspHeaderCount = 64;
    internal const int MaxLspHeaderBytes = 64 * 1024;
    internal const int MaxPositionDocumentBytes = 4 * 1024 * 1024;
    internal const int MaxLiveDocuments = 64;
    internal const int MaxTextDocumentUriChars = McpBoundedText.MaxResourceUriChars;
    internal const int MaxLspRequestIdRawBytes = 4 * 1024;
    internal const int MaxJsonDepth = 32;
    internal const int MaxRequestIdStringChars = 256;
    internal const int MaxDocumentSymbols = 1000;
    internal const int MaxDocumentSymbolDetailChars = 512;
    internal const int MaxDocumentSymbolResponseBytes = 512 * 1024;
    internal const int MaxPositionLineChars = 16 * 1024;
    internal const int MaxCompletionItems = 100;
    internal const int MaxCodeLensItems = 200;
    internal const int MaxInlayHintItems = 200;
    internal const int MaxSemanticTokenItems = 1000;
    internal const int MaxDocumentPathFallbackCandidates = 32;
    internal const int MaxUnknownMethodDiagnosticChars = 240;
    private const int JsonRpcInvalidParamsCode = -32602;
    private const int JsonRpcInternalErrorCode = -32603;
    private const string JsonRpcInvalidParamsMessage = "Invalid params";
    private const string JsonRpcInternalErrorMessage = "Internal error";
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
    private static readonly JsonReaderOptions LspJsonReaderOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };
    private static readonly JsonDocumentOptions LspJsonDocumentOptions = new()
    {
        MaxDepth = MaxJsonDepth,
    };

    private readonly DbReader _reader;
    private readonly string _version;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly string? _projectRoot;
    private readonly StringComparison _pathStringComparison;
    private bool _shutdownRequested;
    private bool _exitRequested;
    private bool _exitRequestedBeforeShutdown;
    private readonly List<string> _workspaceFolders = [];
    private readonly Dictionary<string, string> _liveDocuments;
    private readonly List<string> _liveDocumentOrder = [];

    private readonly record struct PositionTokenContext(string Token, string IndexedPath, string? WorkspaceRoot, int Line, int StartCharacter, int EndCharacter);
    private readonly record struct DocumentSymbolNode(SymbolResult Symbol, JsonObject Item);
    private readonly record struct IndexedDocumentContext(string DocumentPath, string ResolvedPath, string IndexedPath, string? WorkspaceRoot);

    public LspServer(DbReader reader, string version, JsonSerializerOptions jsonOptions, string? projectRoot = null)
    {
        _reader = reader;
        _version = version;
        _jsonOptions = jsonOptions;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : projectRoot;
        _pathStringComparison = PathCasing.ComparisonFor(_projectRoot ?? Environment.CurrentDirectory);
        _liveDocuments = new Dictionary<string, string>(
            _pathStringComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        if (_projectRoot != null)
            _workspaceFolders.Add(Path.GetFullPath(_projectRoot));
    }

    public int Run(Stream input, Stream output) => Run(input, output, CancellationToken.None);

    public int Run(Stream input, Stream output, CancellationToken cancellationToken)
    {
        while (TryReadMessage(input, out var payload, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = HandleMessage(payload);
            if (response != null)
                WriteMessage(output, response.ToJsonString(_jsonOptions));
            if (_exitRequested)
                break;
        }

        return _exitRequestedBeforeShutdown ? CommandExitCodes.UsageError : CommandExitCodes.Success;
    }

    internal JsonObject? HandleMessage(string payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, LspJsonDocumentOptions);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
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
                if (hasId && !TryParseRequestId(payload, idElement, out id, out var requestIdError))
                    return Error(null, -32600, requestIdError);

                if (method == null)
                    return hasId ? Error(id, -32600, "Invalid Request") : null;

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
                    "workspace/symbol" => Result(id, WorkspaceSymbol(root)),
                    "textDocument/documentSymbol" => Result(id, DocumentSymbol(root)),
                    "textDocument/definition" => Result(id, Definition(root, "textDocument/definition")),
                    "textDocument/declaration" => Result(id, Definition(root, "textDocument/declaration")),
                    "textDocument/typeDefinition" => Result(id, Definition(root, "textDocument/typeDefinition")),
                    "textDocument/implementation" => Result(id, Definition(root, "textDocument/implementation")),
                    "textDocument/references" => Result(id, References(root, "textDocument/references")),
                    "textDocument/hover" => Result(id, Hover(root, "textDocument/hover")),
                    "textDocument/completion" => Result(id, Completion(root, "textDocument/completion")),
                    "textDocument/documentHighlight" => Result(id, DocumentHighlight(root, "textDocument/documentHighlight")),
                    "textDocument/semanticTokens/full" => Result(id, SemanticTokensFull(root)),
                    "textDocument/codeLens" => Result(id, CodeLens(root)),
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

    private static bool TryParseRequestId(string payload, JsonElement idElement, out JsonNode? id, out string errorMessage)
    {
        id = null;
        errorMessage = "Invalid Request";
        if (!TryGetTopLevelRequestIdRawByteCount(payload, out var rawIdBytes) || rawIdBytes > MaxLspRequestIdRawBytes)
        {
            errorMessage = $"Request id must be {MaxLspRequestIdRawBytes} raw JSON bytes or fewer.";
            return false;
        }

        var rawId = idElement.GetRawText();
        if (Encoding.UTF8.GetByteCount(rawId) > MaxLspRequestIdRawBytes)
        {
            errorMessage = $"Request id must be {MaxLspRequestIdRawBytes} raw JSON bytes or fewer.";
            return false;
        }

        return TryCloneRequestId(idElement, out id);
    }

    private static bool TryCloneRequestId(JsonElement idElement, out JsonNode? id)
    {
        id = null;
        switch (idElement.ValueKind)
        {
            case JsonValueKind.String:
                var value = idElement.GetString();
                if (value == null || value.Length > MaxRequestIdStringChars)
                    return false;
                id = JsonValue.Create(value);
                return true;

            case JsonValueKind.Number:
                if (!idElement.TryGetInt64(out var number))
                    return false;
                id = JsonValue.Create(number);
                return true;

            case JsonValueKind.Null:
                return true;

            default:
                return false;
        }
    }

    private static bool TryGetTopLevelRequestIdRawByteCount(string payload, out int rawIdBytes)
    {
        rawIdBytes = 0;
        var payloadByteCount = Encoding.UTF8.GetByteCount(payload);
        var buffer = ArrayPool<byte>.Shared.Rent(payloadByteCount);
        try
        {
            _ = Encoding.UTF8.GetBytes(payload.AsSpan(), buffer);
            var reader = new Utf8JsonReader(buffer.AsSpan(0, payloadByteCount), LspJsonReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return true;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                    break;
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    continue;

                var isId = reader.ValueTextEquals("id"u8);
                if (!reader.Read())
                    return false;

                var valueStart = reader.TokenStartIndex;
                reader.Skip();
                if (isId)
                {
                    var rawLength = reader.BytesConsumed - valueStart;
                    if (rawLength > int.MaxValue)
                        return false;
                    rawIdBytes = (int)rawLength;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

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
        foreach (var change in changes.EnumerateArray())
        {
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
            RemoveLiveDocument(key);
        return null;
    }

    private void SetLiveDocumentText(string uri, string text)
    {
        if (!TryGetLiveDocumentKeyFromUri(uri, out var key))
            return;

        if (Encoding.UTF8.GetByteCount(text) > MaxPositionDocumentBytes)
        {
            RemoveLiveDocument(key);
            return;
        }

        EnsureLiveDocumentCapacity(key);
        _liveDocuments[key] = text;
    }

    private void EnsureLiveDocumentCapacity(string key)
    {
        if (_liveDocuments.ContainsKey(key))
            return;

        while (_liveDocuments.Count >= MaxLiveDocuments && _liveDocumentOrder.Count > 0)
        {
            var oldestKey = _liveDocumentOrder[0];
            _liveDocumentOrder.RemoveAt(0);
            _liveDocuments.Remove(oldestKey);
        }

        if (_liveDocuments.Count >= MaxLiveDocuments)
        {
            _liveDocuments.Clear();
            _liveDocumentOrder.Clear();
        }

        _liveDocumentOrder.Add(key);
    }

    private void RemoveLiveDocument(string key)
    {
        _liveDocuments.Remove(key);
        _liveDocumentOrder.RemoveAll(existing => string.Equals(existing, key, _pathStringComparison));
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

    private JsonObject BuildInitializeResult() => new()
    {
        ["capabilities"] = new JsonObject
        {
            ["definitionProvider"] = true,
            ["declarationProvider"] = true,
            ["typeDefinitionProvider"] = true,
            ["implementationProvider"] = true,
            ["referencesProvider"] = true,
            ["documentSymbolProvider"] = true,
            ["workspaceSymbolProvider"] = true,
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
            ["codeLensProvider"] = new JsonObject
            {
                ["resolveProvider"] = false,
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

    private JsonArray WorkspaceSymbol(JsonElement root)
    {
        var query = GetString(root, "params", "query");
        if (query != null && query.Length > QueryLimits.MaxQueryLength)
            throw new ArgumentException(QueryLimits.FormatQueryTooLongError());

        var limit = GetLimit(root, DefaultLimit, MaxWorkspaceSymbols, "params", "limit")
            ?? GetLimit(root, DefaultLimit, MaxWorkspaceSymbols, "params", "maxResults")
            ?? DefaultLimit;
        IReadOnlyList<SymbolResult> symbols = limit == 0 ? [] : _reader.SearchSymbols(query, limit);
        var array = new JsonArray();
        foreach (var symbol in symbols)
            array.Add((JsonNode)ToWorkspaceSymbol(symbol));
        return array;
    }

    private JsonArray DocumentSymbol(JsonElement root)
    {
        var path = GetDocumentPath(root);
        var indexedPath = ResolveIndexedPath(path);
        if (indexedPath == null)
            return [];

        var symbols = _reader.SearchSymbols((string?)null, MaxDocumentSymbols, pathPatterns: [indexedPath])
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .ThenBy(s => s.ContainerName == null ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        return BuildDocumentSymbolTree(symbols);
    }

    private JsonArray BuildDocumentSymbolTree(IReadOnlyList<SymbolResult> symbols)
    {
        var roots = new JsonArray();
        var nodes = new List<DocumentSymbolNode>(symbols.Count);
        foreach (var symbol in symbols)
        {
            var item = ToDocumentSymbol(symbol);
            var node = new DocumentSymbolNode(symbol, item);
            var parent = FindDocumentSymbolParent(nodes, symbol);
            if (parent == null)
                roots.Add((JsonNode)item);
            else
                AddDocumentSymbolChild(parent.Value.Item, item);
            nodes.Add(node);
        }

        TrimDocumentSymbolsToBudget(roots);
        return roots;
    }

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
            array.Add((JsonNode)ToLocation(definition.Path, definition.StartLine, 1, definition.EndLine, 1, GetLocationWorkspaceRoot(definition.Path, context)));
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
        var analysis = ResolveLspReferences(context);
        var array = new JsonArray();
        var seenLocations = new HashSet<string>(StringComparer.Ordinal);
        if (includeDeclaration)
        {
            foreach (var definition in ResolveLspDefinitions(context))
                AddLocation(array, seenLocations, definition.Path, definition.StartLine, 1, definition.EndLine, 1, context);
        }

        foreach (var reference in analysis.References)
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
            AddDocumentHighlight(array, seenRanges, definition.StartLine, 1, definition.EndLine, 1);

        foreach (var reference in ResolveLspReferences(context).References.Where(reference => string.Equals(reference.Path, context.IndexedPath, StringComparison.Ordinal)))
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

        var symbols = GetDocumentSymbols(document.IndexedPath, MaxSemanticTokenItems)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol.Name))
            .Take(MaxSemanticTokenItems)
            .Select(symbol => BuildSemanticToken(document, symbol))
            .Where(token => token.HasValue)
            .Select(token => token!.Value)
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

    private JsonArray CodeLens(JsonElement root)
    {
        if (!TryResolveIndexedDocument(root, out var document))
            return [];

        var array = new JsonArray();
        foreach (var symbol in GetDocumentSymbols(document.IndexedPath, MaxCodeLensItems).Take(MaxCodeLensItems))
            array.Add((JsonNode)ToCodeLens(symbol));
        return array;
    }

    private JsonArray InlayHint(JsonElement root)
    {
        if (!TryResolveIndexedDocument(root, out var document))
            return [];

        var array = new JsonArray();
        foreach (var symbol in GetDocumentSymbols(document.IndexedPath, MaxInlayHintItems)
            .Where(symbol => !string.IsNullOrWhiteSpace(symbol.ReturnType))
            .Take(MaxInlayHintItems))
        {
            array.Add((JsonNode)ToInlayHint(document, symbol));
        }
        return array;
    }

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

    private static string FormatHoverText(SymbolResult symbol)
    {
        var builder = new StringBuilder();
        builder.Append(symbol.Kind).Append(' ').Append(symbol.Name);
        if (!string.IsNullOrWhiteSpace(symbol.Signature))
            builder.AppendLine().Append(symbol.Signature);
        builder.AppendLine().Append(symbol.Path).Append(':').Append(symbol.Line.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(symbol.ContainerName))
            builder.AppendLine().Append("container: ").Append(symbol.ContainerName);
        if (!string.IsNullOrWhiteSpace(symbol.ReturnType))
            builder.AppendLine().Append("returns: ").Append(symbol.ReturnType);
        return builder.ToString();
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

    private JsonObject ToCodeLens(SymbolResult symbol) => new()
    {
        ["range"] = ToRange(symbol.Line, 1, symbol.Line, 1),
        ["command"] = new JsonObject
        {
            ["title"] = $"cdidx: {symbol.Kind}",
            ["command"] = "cdidx.showSymbol",
            ["arguments"] = new JsonArray(new JsonObject
            {
                ["name"] = symbol.Name,
                ["kind"] = symbol.Kind,
                ["path"] = symbol.Path,
                ["line"] = symbol.Line,
            }),
        },
    };

    private JsonObject ToInlayHint(IndexedDocumentContext document, SymbolResult symbol)
    {
        var startCharacter = FindSymbolStartCharacter(document, symbol);
        return new JsonObject
        {
            ["position"] = ToPosition(symbol.Line, startCharacter + symbol.Name.Length + 1),
            ["label"] = ": " + symbol.ReturnType,
            ["kind"] = 1,
            ["paddingLeft"] = true,
        };
    }

    private readonly record struct SemanticToken(int Line, int StartCharacter, int Length, int TokenType, int TokenModifiers);

    private SemanticToken? BuildSemanticToken(IndexedDocumentContext document, SymbolResult symbol)
    {
        var line = Math.Max(symbol.Line, symbol.StartLine);
        if (line <= 0)
            return null;

        var startCharacter = FindSymbolStartCharacter(document, symbol);
        var length = Math.Max(symbol.Name.Length, 1);
        return new SemanticToken(
            line - 1,
            startCharacter,
            length,
            SemanticTokenType(symbol.Kind),
            1 << 1);
    }

    private int FindSymbolStartCharacter(IndexedDocumentContext document, SymbolResult symbol)
    {
        var line = Math.Max(symbol.Line, symbol.StartLine);
        if (line <= 0 || string.IsNullOrWhiteSpace(symbol.Name))
            return 0;

        return TryReadPositionLine(document.ResolvedPath, line - 1, out var sourceLine, out _)
            ? Math.Max(0, sourceLine.IndexOf(symbol.Name, StringComparison.Ordinal))
            : 0;
    }

    private static int SemanticTokenType(string kind) => kind switch
    {
        "namespace" => 0,
        "class" => 2,
        "enum" => 3,
        "interface" => 4,
        "struct" => 5,
        "property" => 9,
        "function" or "test.method" => 13,
        _ => 8,
    };

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

    private DocumentSymbolNode? FindDocumentSymbolParent(IReadOnlyList<DocumentSymbolNode> nodes, SymbolResult symbol)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
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

            return nodes[i];
        }

        if (symbol.ContainerName != null)
            return null;

        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var candidate = nodes[i].Symbol;
            if (ContainsDocumentSymbol(candidate, symbol))
                return nodes[i];
        }

        return null;
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

    private void TrimDocumentSymbolsToBudget(JsonArray roots)
    {
        while (roots.Count > 0
            && Encoding.UTF8.GetByteCount(roots.ToJsonString(_jsonOptions)) > MaxDocumentSymbolResponseBytes
            && RemoveLastDocumentSymbol(roots))
        {
        }
    }

    private static bool RemoveLastDocumentSymbol(JsonArray symbols)
    {
        if (symbols.Count == 0)
            return false;

        if (symbols[symbols.Count - 1] is JsonObject last
            && last["children"] is JsonArray children
            && children.Count > 0)
        {
            if (RemoveLastDocumentSymbol(children))
            {
                if (children.Count == 0)
                    last.Remove("children");
                return true;
            }
        }

        symbols.RemoveAt(symbols.Count - 1);
        return true;
    }

    private List<DefinitionResult> ResolveLspDefinitions(PositionTokenContext context)
    {
        var localDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true, pathPatterns: [context.IndexedPath]);
        if (localDefinitions.Count > 0)
            return localDefinitions;

        var workspaceDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true);
        return workspaceDefinitions;
    }

    private SymbolAnalysisResult ResolveLspReferences(PositionTokenContext context)
    {
        var localDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true, pathPatterns: [context.IndexedPath]);
        if (localDefinitions.Count > 0)
            return _reader.AnalyzeSymbol(context.Token, DefaultLimit, pathPatterns: [context.IndexedPath], exact: true);

        var workspaceDefinitions = _reader.GetDefinitions(context.Token, DefaultLimit, exact: true);
        if (workspaceDefinitions.Count == 0 || !HasSingleLspDefinitionTarget(workspaceDefinitions))
            return _reader.AnalyzeSymbol(context.Token, DefaultLimit, pathPatterns: [context.IndexedPath], exact: true);

        return _reader.AnalyzeSymbol(context.Token, DefaultLimit, exact: true);
    }

    private static bool HasSingleLspDefinitionTarget(IReadOnlyList<DefinitionResult> definitions)
    {
        if (definitions.Count <= 1)
            return true;

        var firstKey = BuildLspDefinitionTargetKey(definitions[0]);
        return definitions.Skip(1).All(definition => string.Equals(BuildLspDefinitionTargetKey(definition), firstKey, StringComparison.Ordinal));
    }

    private static string BuildLspDefinitionTargetKey(DefinitionResult definition)
        => string.Join('\0', definition.Path, definition.Kind, definition.ContainerKind, definition.ContainerName, definition.Name);

    private bool TryExtractPositionToken(JsonElement root, out PositionTokenContext context, out string? failureReason)
    {
        context = default;
        failureReason = null;
        var path = GetDocumentPath(root);
        var line = GetInt32(root, "params", "position", "line");
        var character = GetInt32(root, "params", "position", "character");
        if (line < 0 || character < 0)
        {
            failureReason = FailureInvalidPosition;
            return false;
        }

        if (!TryResolveDocumentPath(path, out var resolvedPath, out var projectRelativePath, out var workspaceRoot, out failureReason))
            return false;

        var indexedPath = ResolveIndexedPath(path, resolvedPath, projectRelativePath, workspaceRoot);
        if (indexedPath == null)
        {
            failureReason = FailureFileNotIndexed;
            return false;
        }

        var indexedPathRoot = _projectRoot == null ? workspaceRoot : null;
        if (!TryResolveIndexedFilePath(indexedPath, indexedPathRoot, out var indexedFullPath))
        {
            failureReason = FailureIndexedFileUnresolved;
            return false;
        }

        if (!string.Equals(resolvedPath, indexedFullPath, _pathStringComparison))
        {
            failureReason = FailurePathCasingMismatch;
            return false;
        }

        if (!TryReadPositionLine(indexedFullPath, line, out var sourceLine, out failureReason))
            return false;

        var token = ExtractTokenAtUtf16Position(sourceLine, character);
        if (string.IsNullOrWhiteSpace(token))
        {
            failureReason = FailureNoTokenAtPosition;
            return false;
        }

        var (startCharacter, endCharacter) = FindTokenRangeAtUtf16Position(sourceLine, character);
        context = new PositionTokenContext(token, indexedPath, workspaceRoot, line, startCharacter, endCharacter);
        return true;
    }

    private bool TryResolveIndexedDocument(JsonElement root, out IndexedDocumentContext context)
    {
        context = default;
        var documentPath = GetDocumentPath(root);
        if (!TryResolveDocumentPath(documentPath, out var resolvedPath, out var projectRelativePath, out var workspaceRoot))
            return false;

        var indexedPath = ResolveIndexedPath(documentPath, resolvedPath, projectRelativePath, workspaceRoot);
        if (indexedPath == null)
            return false;

        var indexedPathRoot = _projectRoot == null ? workspaceRoot : null;
        if (!TryResolveIndexedFilePath(indexedPath, indexedPathRoot, out var indexedFullPath))
            return false;

        if (!string.Equals(resolvedPath, indexedFullPath, _pathStringComparison))
            return false;

        context = new IndexedDocumentContext(documentPath, resolvedPath, indexedPath, workspaceRoot);
        return true;
    }

    private List<SymbolResult> GetDocumentSymbols(string indexedPath, int limit)
        => _reader.SearchSymbols((string?)null, limit, pathPatterns: [indexedPath])
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .ThenBy(s => s.ContainerName == null ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    private bool TryReadPositionLine(string path, int targetLine, out string sourceLine, out string? failureReason)
    {
        if (_liveDocuments.TryGetValue(Path.GetFullPath(path), out var liveText))
            return TryReadPositionLineFromText(liveText, targetLine, out sourceLine, out failureReason);

        return TryReadPositionLineFromFile(path, targetLine, out sourceLine, out failureReason);
    }

    private static bool TryReadPositionLineFromText(string text, int targetLine, out string sourceLine, out string? failureReason)
    {
        sourceLine = string.Empty;
        failureReason = null;
        if (targetLine < 0)
        {
            failureReason = FailureInvalidPosition;
            return false;
        }

        var currentLine = 0;
        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var isLineBreak = !atEnd && (text[i] == '\r' || text[i] == '\n');
            if (!atEnd && !isLineBreak)
                continue;

            if (currentLine == targetLine)
            {
                var length = i - lineStart;
                if (length > MaxPositionLineChars)
                {
                    failureReason = FailurePositionLineTooLong;
                    return false;
                }

                sourceLine = text.Substring(lineStart, length);
                return true;
            }

            if (atEnd)
                break;

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            currentLine++;
            lineStart = i + 1;
        }

        failureReason = FailurePositionLineMissing;
        return false;
    }

    private static bool TryReadPositionLineFromFile(string path, int targetLine, out string sourceLine, out string? failureReason)
    {
        sourceLine = string.Empty;
        failureReason = null;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxPositionDocumentBytes)
            {
                failureReason = FailurePositionFileTooLarge;
                return false;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var currentLine = 0;
            var currentLineLength = 0;
            StringBuilder? builder = targetLine == 0 ? new StringBuilder() : null;
            while (true)
            {
                var next = reader.Read();
                if (stream.Position > MaxPositionDocumentBytes)
                {
                    failureReason = FailurePositionFileTooLarge;
                    return false;
                }

                if (next < 0)
                {
                    if (currentLine == targetLine && currentLineLength <= MaxPositionLineChars && builder != null)
                    {
                        sourceLine = builder.ToString();
                        return true;
                    }

                    failureReason = FailurePositionLineMissing;
                    return false;
                }

                var c = (char)next;
                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && reader.Peek() == '\n')
                    {
                        reader.Read();
                        if (stream.Position > MaxPositionDocumentBytes)
                        {
                            failureReason = FailurePositionFileTooLarge;
                            return false;
                        }
                    }

                    if (currentLine == targetLine)
                    {
                        sourceLine = builder?.ToString() ?? string.Empty;
                        return true;
                    }

                    currentLine++;
                    currentLineLength = 0;
                    builder = currentLine == targetLine ? new StringBuilder() : null;
                    continue;
                }

                currentLineLength++;
                if (currentLineLength > MaxPositionLineChars)
                {
                    if (currentLine == targetLine)
                    {
                        failureReason = FailurePositionLineTooLong;
                        return false;
                    }
                    continue;
                }

                builder?.Append(c);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            failureReason = FailurePositionFileUnreadable;
            return false;
        }
    }

    internal static string? ExtractTokenAtUtf16Position(string line, int character)
    {
        if (character < 0)
            return null;
        var index = Math.Min(character, line.Length);
        while (index > 0 && index == line.Length)
            index--;
        if (index < line.Length && !IsTokenChar(line[index]) && index > 0 && IsTokenChar(line[index - 1]))
            index--;
        if (index >= line.Length || !IsTokenChar(line[index]))
            return null;

        var start = index;
        while (start > 0 && IsTokenChar(line[start - 1]))
            start--;
        var end = index + 1;
        while (end < line.Length && IsTokenChar(line[end]))
            end++;
        return line[start..end].TrimStart('@');
    }

    private static (int Start, int End) FindTokenRangeAtUtf16Position(string line, int character)
    {
        if (character < 0)
            return (0, 0);
        var index = Math.Min(character, line.Length);
        while (index > 0 && index == line.Length)
            index--;
        if (index < line.Length && !IsTokenChar(line[index]) && index > 0 && IsTokenChar(line[index - 1]))
            index--;
        if (index >= line.Length || !IsTokenChar(line[index]))
            return (Math.Max(0, Math.Min(character, line.Length)), Math.Max(0, Math.Min(character, line.Length)));

        var start = index;
        while (start > 0 && IsTokenChar(line[start - 1]))
            start--;
        var end = index + 1;
        while (end < line.Length && IsTokenChar(line[end]))
            end++;
        return (start, end);
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';

    private bool MatchesDocumentPath(string indexedPath, string documentPath, string? projectRelativePath, string resolvedPath, string? workspaceRoot)
    {
        if (TryResolveIndexedFilePath(indexedPath, null, out var indexedFullPath)
            && string.Equals(resolvedPath, indexedFullPath, _pathStringComparison))
            return true;

        if (Path.IsPathRooted(indexedPath))
            return false;

        var normalizedIndexed = indexedPath.Replace('\\', '/');
        if (projectRelativePath != null)
            return _projectRoot == null
                && workspaceRoot != null
                && string.Equals(normalizedIndexed, projectRelativePath.Replace('\\', '/'), _pathStringComparison);

        if (string.Equals(indexedPath, documentPath, StringComparison.Ordinal))
            return true;

        if (_projectRoot == null && workspaceRoot == null)
            return false;

        var normalizedDocument = documentPath.Replace('\\', '/');
        return normalizedDocument.EndsWith("/" + normalizedIndexed, StringComparison.Ordinal);
    }

    private string? ResolveIndexedPath(string documentPath)
    {
        if (!TryResolveDocumentPath(documentPath, out var resolvedPath, out var projectRelativePath, out var workspaceRoot))
            return null;

        return ResolveIndexedPath(documentPath, resolvedPath, projectRelativePath, workspaceRoot);
    }

    private string? ResolveIndexedPath(string documentPath, string resolvedPath, string? projectRelativePath, string? workspaceRoot)
    {
        if (projectRelativePath != null)
        {
            var exactPath = projectRelativePath.Replace('\\', '/');
            var exactFile = _reader.GetFileByPath(exactPath);
            if (exactFile != null && MatchesDocumentPath(exactFile.Path, documentPath, projectRelativePath, resolvedPath, workspaceRoot))
                return exactFile.Path;
        }

        var fileName = Path.GetFileName(documentPath);
        if (string.IsNullOrEmpty(fileName))
            fileName = Path.GetFileName(resolvedPath);
        if (string.IsNullOrEmpty(fileName))
            return null;

        var files = _reader.ListFiles(fileName, MaxDocumentPathFallbackCandidates);
        var matches = files
            .Where(file => MatchesDocumentPath(file.Path, documentPath, projectRelativePath, resolvedPath, workspaceRoot))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0].Path : null;
    }

    private bool TryResolveDocumentPath(string documentPath, out string resolvedPath, out string? projectRelativePath) =>
        TryResolveDocumentPath(documentPath, out resolvedPath, out projectRelativePath, out _, out _);

    private bool TryResolveDocumentPath(
        string documentPath,
        out string resolvedPath,
        out string? projectRelativePath,
        out string? workspaceRoot) =>
        TryResolveDocumentPath(documentPath, out resolvedPath, out projectRelativePath, out workspaceRoot, out _);

    private bool TryResolveDocumentPath(
        string documentPath,
        out string resolvedPath,
        out string? projectRelativePath,
        out string? workspaceRoot,
        out string? failureReason)
    {
        resolvedPath = string.Empty;
        projectRelativePath = null;
        workspaceRoot = null;
        failureReason = null;
        try
        {
            resolvedPath = Path.IsPathRooted(documentPath)
                ? Path.GetFullPath(documentPath)
                : Path.GetFullPath(documentPath, _projectRoot ?? Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            failureReason = FailureDocumentPathUnresolved;
            return false;
        }

        if (_workspaceFolders.Count == 0)
            return true;

        if (TryGetWorkspaceRelativePath(resolvedPath, out projectRelativePath, out workspaceRoot))
            return true;

        failureReason = FailureOutsideProject;
        return false;
    }

    private bool TryResolveIndexedFilePath(string indexedPath, out string resolvedPath)
        => TryResolveIndexedFilePath(indexedPath, null, out resolvedPath);

    private bool TryResolveIndexedFilePath(string indexedPath, string? workspaceRoot, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            resolvedPath = Path.IsPathRooted(indexedPath)
                ? Path.GetFullPath(indexedPath)
                : Path.GetFullPath(indexedPath, workspaceRoot ?? _projectRoot ?? Environment.CurrentDirectory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool TryGetProjectRelativePath(string resolvedPath, out string? relativePath)
    {
        relativePath = null;
        if (_projectRoot == null)
            return false;

        return TryGetRelativePath(Path.GetFullPath(_projectRoot), resolvedPath, out relativePath);
    }

    private bool TryGetWorkspaceRelativePath(string resolvedPath, out string? relativePath, out string? workspaceRoot)
    {
        relativePath = null;
        workspaceRoot = null;
        foreach (var candidateRoot in _workspaceFolders)
        {
            if (!TryGetRelativePath(candidateRoot, resolvedPath, out var candidateRelativePath))
                continue;

            relativePath = candidateRelativePath;
            workspaceRoot = candidateRoot;
            return true;
        }

        return false;
    }

    private static bool TryGetRelativePath(string root, string resolvedPath, out string? relativePath)
    {
        relativePath = null;
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var normalizedPath = Path.GetFullPath(resolvedPath);
            if (PathCasing.PathsEqual(normalizedRoot, normalizedPath)
                || !PathCasing.IsPathEqualOrParent(normalizedRoot, normalizedPath))
            {
                return false;
            }

            var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
            if (relative == "."
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return false;
            }

            relativePath = relative;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private JsonObject ToWorkspaceSymbol(SymbolResult symbol) => new()
    {
        ["name"] = symbol.Name,
        ["kind"] = SymbolKind(symbol.Kind),
        ["location"] = ToLocation(symbol.Path, symbol.StartLine, 1, symbol.EndLine, 1),
        ["containerName"] = symbol.ContainerName,
    };

    private static JsonObject ToDocumentSymbol(SymbolResult symbol) => new()
    {
        ["name"] = symbol.Name,
        ["kind"] = SymbolKind(symbol.Kind),
        ["range"] = ToRange(symbol.StartLine, 1, symbol.EndLine, 1),
        ["selectionRange"] = ToRange(symbol.Line, 1, symbol.Line, 1),
        ["detail"] = TruncateDocumentSymbolDetail(symbol.Signature),
    };

    private static string? TruncateDocumentSymbolDetail(string? detail)
    {
        if (detail == null || detail.Length <= MaxDocumentSymbolDetailChars)
            return detail;
        return detail[..(MaxDocumentSymbolDetailChars - "...".Length)] + "...";
    }

    private JsonObject ToLocation(string path, int startLine, int startColumn, int endLine, int endColumn, string? workspaceRoot = null) => new()
    {
        ["uri"] = PathToUri(path, workspaceRoot ?? _projectRoot),
        ["range"] = ToRange(startLine, startColumn, endLine, endColumn),
    };

    private static JsonObject ToRange(int startLine, int startColumn, int endLine, int endColumn) => new()
    {
        ["start"] = new JsonObject
        {
            ["line"] = Math.Max(startLine - 1, 0),
            ["character"] = Math.Max(startColumn - 1, 0),
        },
        ["end"] = new JsonObject
        {
            ["line"] = Math.Max(endLine - 1, 0),
            ["character"] = Math.Max(endColumn - 1, 0),
        },
    };

    private static JsonObject ToPosition(int line, int column) => new()
    {
        ["line"] = Math.Max(line - 1, 0),
        ["character"] = Math.Max(column - 1, 0),
    };

    private static int SymbolKind(string kind) => kind switch
    {
        "class" => 5,
        "function" or "test.method" => 12,
        "property" => 7,
        "enum" => 10,
        "interface" => 11,
        "namespace" => 3,
        "struct" => 23,
        _ => 13,
    };

    private static string GetDocumentPath(JsonElement root)
    {
        var uri = GetTextDocumentUri(root);
        return UriToPath(uri);
    }

    private static string GetTextDocumentUri(JsonElement root)
    {
        if (!TryGet(root, out var value, "params", "textDocument", "uri") || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("textDocument.uri must be a string.");

        var uri = value.GetString();
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("textDocument.uri is required.");
        if (uri.Length > MaxTextDocumentUriChars)
            throw new ArgumentException(
                $"textDocument.uri is too long. Max length is {MaxTextDocumentUriChars} characters; actual length is {uri.Length}.");
        return uri;
    }

    private static string? GetString(JsonElement root, params string[] path)
    {
        if (!TryGet(root, out var value, path) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static bool? GetBool(JsonElement root, params string[] path)
    {
        if (!TryGet(root, out var value, path))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int? GetLimit(JsonElement root, int defaultLimit, int maxLimit, params string[] path)
    {
        if (!TryGet(root, out var value, path))
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var limit))
            return defaultLimit;
        return Math.Clamp(limit, 0, maxLimit);
    }

    private static int GetInt32(JsonElement root, params string[] path)
    {
        if (!TryGet(root, out var value, path) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            return -1;
        return result;
    }

    private static bool TryGet(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
                return false;
        }
        return true;
    }

    internal static string PathToUri(string path, string? projectRoot = null)
    {
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, projectRoot ?? Environment.CurrentDirectory);
        return new Uri(fullPath).AbsoluteUri;
    }

    internal static string UriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
            throw new ArgumentException("textDocument.uri must be an absolute file URI.");
        return parsed.LocalPath;
    }

    private void CaptureInitializeWorkspaceFolders(JsonElement root)
    {
        if (!TryGet(root, out var folders, "params", "workspaceFolders") || folders.ValueKind != JsonValueKind.Array)
            return;

        foreach (var folder in folders.EnumerateArray())
        {
            if (_workspaceFolders.Count >= MaxWorkspaceFolders)
                break;
            if (TryGetWorkspaceFolderPath(folder, out var path)
                && !_workspaceFolders.Any(existing => string.Equals(existing, path, _pathStringComparison)))
            {
                _workspaceFolders.Add(path);
            }
        }

        Activity.Current?.SetTag("lsp.workspace_folder_count", _workspaceFolders.Count);
    }

    private static bool TryGetWorkspaceFolderPath(JsonElement folder, out string path)
    {
        path = string.Empty;
        if (folder.ValueKind != JsonValueKind.Object
            || !folder.TryGetProperty("uri", out var uriElement)
            || uriElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > MaxTextDocumentUriChars)
            return false;

        try
        {
            path = Path.GetFullPath(UriToPath(uri));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static JsonObject Result(JsonNode? id, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    internal static bool TryReadMessage(Stream input, out string payload) =>
        TryReadMessage(input, out payload, CancellationToken.None);

    internal static bool TryReadMessage(Stream input, out string payload, CancellationToken cancellationToken)
    {
        payload = string.Empty;
        var contentLength = -1;
        var hasContentLength = false;
        var headerCount = 0;
        var headerBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = ReadAsciiLine(input, cancellationToken);
            if (line == null)
                return false;
            if (line.Length == 0)
                break;
            headerCount++;
            headerBytes += line.Length;
            if (headerCount > MaxLspHeaderCount || headerBytes > MaxLspHeaderBytes)
                return false;
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (hasContentLength)
                    return false;
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 0
                    || parsed > MaxLspFrameBytes)
                {
                    return false;
                }

                hasContentLength = true;
                contentLength = parsed;
            }
        }

        if (contentLength < 0)
            return false;

        var buffer = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            var offset = 0;
            while (offset < contentLength)
            {
                var read = Read(input, buffer, offset, contentLength - offset, cancellationToken);
                if (read == 0)
                    return false;
                offset += read;
            }
            payload = Encoding.UTF8.GetString(buffer, 0, contentLength);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static void WriteMessage(Stream output, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        output.Write(header);
        output.Write(body);
        output.Flush();
    }

    private static string? ReadAsciiLine(Stream input, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxLspHeaderLineBytes + 1);
        var length = 0;
        try
        {
            while (true)
            {
                var read = Read(input, buffer, length, 1, cancellationToken);
                if (read == 0)
                    return length == 0 ? null : Encoding.ASCII.GetString(buffer, 0, length);

                var value = buffer[length];
                if (value == '\n')
                    break;
                if (value != '\r')
                {
                    if (length >= MaxLspHeaderLineBytes)
                        return null;
                    length++;
                }
            }

            return Encoding.ASCII.GetString(buffer, 0, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int Read(Stream input, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return input.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _ = _shutdownRequested;
    }
}
