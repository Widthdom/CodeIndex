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
    private JsonObject ToWorkspaceSymbol(
        SymbolResult symbol,
        (int Line, int StartColumn, int EndColumn) identifier)
    {
        return new JsonObject
        {
            ["name"] = symbol.Name,
            ["kind"] = SymbolKind(symbol.Kind),
            ["location"] = ToLocation(symbol.Path, identifier.Line, identifier.StartColumn, identifier.Line, identifier.EndColumn),
            ["containerName"] = symbol.ContainerName,
        };
    }

    private JsonObject ToDocumentSymbol(
        IndexedDocumentContext document,
        SymbolResult symbol,
        Dictionary<int, string?> lineCache)
    {
        var identifier = GetSymbolIdentifierPosition(symbol, document.ResolvedPath, lineCache);
        var rangeStartLine = symbol.StartLine > 0 ? Math.Min(symbol.StartLine, identifier.Line) : identifier.Line;
        var rangeEndLine = symbol.EndLine > 0 ? Math.Max(symbol.EndLine, identifier.Line) : identifier.Line;
        var rangeEndColumn = rangeEndLine == identifier.Line ? identifier.EndColumn : 1;
        return new JsonObject
        {
            ["name"] = symbol.Name,
            ["kind"] = SymbolKind(symbol.Kind),
            ["range"] = ToRange(rangeStartLine, 1, rangeEndLine, rangeEndColumn),
            ["selectionRange"] = ToRange(identifier.Line, identifier.StartColumn, identifier.Line, identifier.EndColumn),
            ["detail"] = TruncateDocumentSymbolDetail(symbol.Signature),
        };
    }

    private JsonObject ToDocumentSymbolInformation(
        IndexedDocumentContext document,
        SymbolResult symbol,
        Dictionary<int, string?> lineCache)
    {
        var identifier = GetSymbolIdentifierPosition(symbol, document.ResolvedPath, lineCache);
        return new JsonObject
        {
            ["name"] = symbol.Name,
            ["kind"] = SymbolKind(symbol.Kind),
            ["location"] = ToLocation(
                symbol.Path,
                identifier.Line,
                identifier.StartColumn,
                identifier.Line,
                identifier.EndColumn,
                document.WorkspaceRoot),
            ["containerName"] = symbol.ContainerName,
        };
    }

    private (int Line, int StartColumn, int EndColumn) GetSymbolIdentifierPosition(SymbolResult symbol)
    {
        var resolvedPath = TryResolveIndexedFilePath(symbol.Path, out var path) ? path : null;
        return GetSymbolIdentifierPosition(symbol, resolvedPath);
    }

    private (int Line, int StartColumn, int EndColumn) GetSymbolIdentifierPosition(
        SymbolResult symbol,
        PositionTokenContext context)
    {
        var indexedPathRoot = _projectRoot == null ? context.WorkspaceRoot : null;
        var resolvedPath = TryResolveIndexedFilePath(symbol.Path, indexedPathRoot, out var path) ? path : null;
        return GetSymbolIdentifierPosition(symbol, resolvedPath);
    }

    private (int Line, int StartColumn, int EndColumn) GetSymbolIdentifierPosition(
        SymbolResult symbol,
        string? resolvedPath,
        Dictionary<int, string?>? lineCache = null)
    {
        var line = symbol.Line > 0 ? symbol.Line : Math.Max(1, symbol.StartLine);
        var startCharacter = resolvedPath == null
            ? Math.Max(0, symbol.StartColumn ?? 0)
            : FindSymbolStartCharacter(resolvedPath, symbol, lineCache);
        return (line, startCharacter + 1, startCharacter + Math.Max(symbol.Name.Length, 1) + 1);
    }

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
        => CodeIndex.FileUriPolicy.PathToFileUri(path, projectRoot);

    internal static string UriToPath(string uri)
        => CodeIndex.FileUriPolicy.AbsoluteFileUriToPath(uri);

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

    /// <summary>
    /// Compatibility wrapper that reads without caller cancellation. Prefer <see cref="TryReadMessageAsync"/>
    /// for cancellable transports.
    /// caller cancellation を持たない互換 wrapper。キャンセル可能な transport では
    /// <see cref="TryReadMessageAsync"/> を使う。
    /// </summary>
    internal static bool TryReadMessage(Stream input, out string payload) =>
        TryReadMessage(input, out payload, CancellationToken.None);

    internal static bool TryReadMessage(Stream input, out string payload, CancellationToken cancellationToken)
        => TryReadMessage(input, out payload, out _, cancellationToken);

    internal static bool TryReadMessage(
        Stream input,
        out string payload,
        out LspMessageReadDiagnostic? diagnostic,
        CancellationToken cancellationToken = default)
    {
        var success = LspProtocol.TryReadMessage(input, out payload, out var protocolDiagnostic, cancellationToken);
        diagnostic = protocolDiagnostic.HasValue ? ToServerDiagnostic(protocolDiagnostic.Value) : null;
        return success;
    }

    internal static async ValueTask<MessageReadResult> TryReadMessageAsync(
        Stream input,
        CancellationToken cancellationToken = default)
    {
        var result = await LspProtocol.TryReadMessageAsync(input, cancellationToken).ConfigureAwait(false);
        return new MessageReadResult(result.Success, result.Payload);
    }

    private static LspMessageReadDiagnostic ToServerDiagnostic(LspProtocol.ReadDiagnostic diagnostic)
        => new(diagnostic.Code, diagnostic.Message, diagnostic.ContentLength, diagnostic.MaxContentLength);

    private async Task WriteResponseMessageAsync(
        Stream output,
        SemaphoreSlim outputGate,
        JsonObject response,
        CancellationToken cancellationToken)
    {
        await outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = response.ToJsonString(_jsonOptions);
            if (await LspProtocol.TryWriteMessageAsync(output, payload, cancellationToken).ConfigureAwait(false))
                return;

            var id = response["id"]?.DeepClone();
            var errorPayload = Error(id, JsonRpcInternalErrorCode, "Response too large").ToJsonString(_jsonOptions);
            if (!await LspProtocol.TryWriteMessageAsync(output, errorPayload, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("LSP response error exceeded the response frame byte limit.");
        }
        finally
        {
            outputGate.Release();
        }
    }

    private async Task WriteServerNotificationAsync(
        Stream output,
        SemaphoreSlim outputGate,
        JsonObject notification,
        CancellationToken cancellationToken)
    {
        await outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = notification.ToJsonString(_jsonOptions);
            if (!await LspProtocol.TryWriteMessageAsync(output, payload, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("LSP server notification exceeded the response frame byte limit.");
        }
        finally
        {
            outputGate.Release();
        }
    }

    internal static void WriteMessage(Stream output, string payload) =>
        LspProtocol.WriteMessage(output, payload);

    internal static bool TryWriteMessage(Stream output, string payload, out int bodyBytes) =>
        LspProtocol.TryWriteMessage(output, payload, out bodyBytes);

    public void Dispose()
    {
        lock (_sessionStateGate)
        {
            _sessionState = LspSessionState.Exited;
            while (_activeSessionDispatches != 0)
                Monitor.Wait(_sessionStateGate);
        }

        DisposeOwnedResourcesOnce();
    }

    private void DisposeOwnedResourcesOnce()
    {
        if (Interlocked.Exchange(ref _ownedResourcesDisposed, 1) != 0)
            return;

        var ownedQueryDb = Interlocked.Exchange(ref _ownedQueryDb, null);
        if (ownedQueryDb == null)
            return;

        _reader.Dispose();
        ownedQueryDb.Dispose();
        Interlocked.Increment(ref _ownedResourceDisposeCount);
    }
}
